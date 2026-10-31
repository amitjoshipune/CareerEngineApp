using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Npgsql;

internal class Program
{
    private static async Task Main(string[] args)
    {
        // 1. Configuration
        string windowsMachineIp = "192.168.1.8";

        string ollamaUrl =
            $"http://{windowsMachineIp}:11434/api/generate";

        string dbConnectionString =
            "Host=localhost;Username=postgres;Password='123';Database=career_engine_db";

        string incomingJdDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "CareerEngine",
            "IncomingJDs");

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("=====================================================");
        Console.WriteLine("    AUTONOMOUS CAREER ENGINE: C# BATCH INGESTOR");
        Console.WriteLine("=====================================================\n");
        Console.ResetColor();

        // 2. Ensure the incoming folder exists
        if (!Directory.Exists(incomingJdDir))
        {
            Console.WriteLine(
                $"[-] Target folder not found. Creating: {incomingJdDir}");

            Directory.CreateDirectory(incomingJdDir);

            Console.WriteLine(
                "Folder created. Add job description text files and run again.");

            return;
        }

        // 3. Find incoming job descriptions
        string[] targets = Directory.GetFiles(incomingJdDir, "*.txt");

        if (targets.Length == 0)
        {
            Console.WriteLine(
                $"[-] No text files found in {incomingJdDir}. Drop your JDs there!");

            return;
        }

        // 4. Configure the HTTP client
        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(3)
        };

        // 5. Instructions for the AI model
        string engineSystemPrompt =
            """
            You are a strict enterprise job-description extraction system.

            Analyze the supplied job description and return a JSON object only.
            Do not include Markdown, code fences, or conversational explanations.

            Use exactly this JSON structure:
            {
              "JobTitle": "string value",
              "CoreSkills": ["value1", "value2"],
              "SecondarySkills": ["value1"],
              "ExperienceRange": "string value"
            }

            Rules:
            - JobTitle must be a string.
            - CoreSkills must be an array of strings.
            - SecondarySkills must be an array of strings.
            - ExperienceRange must be a string.
            - Use an empty array when no skills can be identified.
            - Use "Not Specified" when the experience requirement is absent.
            - Do not invent qualifications or requirements that are absent
              from the job description.
            """;

        // 6. Process each job description
        foreach (string file in targets)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(
                $"Reading local target document: {Path.GetFileName(file)}");
            Console.ResetColor();

            try
            {
                string fileContents = await File.ReadAllTextAsync(file);

                if (string.IsNullOrWhiteSpace(fileContents))
                {
                    Console.WriteLine("[-] File is empty. Skipping.\n");
                    continue;
                }

                // 7. Construct the request for Ollama
                var payload = new
                {
                    model = "llama3.2",
                    system = engineSystemPrompt,
                    prompt = fileContents,
                    stream = false,
                    format = "json",
                    options = new
                    {
                        temperature = 0.1
                    }
                };

                Console.WriteLine(
                    "Executing network inference call to Windows AI Core...");

                using HttpResponseMessage response =
                    await httpClient.PostAsJsonAsync(ollamaUrl, payload);

                // Fail if Ollama returns an unsuccessful HTTP status.
                response.EnsureSuccessStatusCode();

                string rawJsonResult =
                    await response.Content.ReadAsStringAsync();

                // 8. Parse Ollama's API response
                using JsonDocument parsedDoc =
                    JsonDocument.Parse(rawJsonResult);

                if (!parsedDoc.RootElement.TryGetProperty(
                        "response", out JsonElement responseElement))
                {
                    throw new InvalidOperationException(
                        "Ollama response does not contain the 'response' field.");
                }

                string? rawAiResponse = responseElement.GetString();

                if (string.IsNullOrWhiteSpace(rawAiResponse))
                {
                    throw new InvalidOperationException(
                        "Ollama returned an empty AI response.");
                }

                Console.WriteLine("Raw AI response:");
                Console.WriteLine(rawAiResponse);
                Console.WriteLine("*====================================================================*");
                // 9. Parse the JSON generated by the AI
                using JsonDocument dataMatrix =
                    JsonDocument.Parse(rawAiResponse);

                Console.WriteLine("AI JSON response:");
                Console.WriteLine(dataMatrix.ToString());
                Console.WriteLine("*====================================================================*");
                Console.WriteLine("Extracting structured data from AI JSON...");

                JsonElement root = dataMatrix.RootElement;

                string extractedTitle =
                    GetRequiredString(root, "JobTitle");
                Console.WriteLine($"Extracted Job Title: {extractedTitle}");

                string extractedExp =
                    GetRequiredString(root, "ExperienceRange");
                Console.WriteLine($"Extracted Experience Range: {extractedExp}");

                List<string> coreSkillsList =
                    GetStringArray(root, "CoreSkills");
                Console.WriteLine($"Extracted Core Skills: {string.Join(", ", coreSkillsList)}");

                List<string> secondarySkillsList =
                    GetStringArray(root, "SecondarySkills");
                Console.WriteLine($"Extracted Secondary Skills: {string.Join(", ", secondarySkillsList)}");

                Console.WriteLine("*====================================================================*");

                // 10. Save the extracted information in PostgreSQL
                Console.WriteLine(
                    "Connecting to PostgreSQL and saving the extracted record...");

                await using var conn =
                    new NpgsqlConnection(dbConnectionString);

                await conn.OpenAsync();

                const string sql =
                    """
                    INSERT INTO job_descriptions
                        (job_title, core_skills, secondary_skills,
                         experience_range, raw_text)
                    VALUES
                        (@title, @core, @sec, @exp, @raw);
                    """;

                await using var cmd = new NpgsqlCommand(sql, conn);

                cmd.Parameters.AddWithValue(
                    "title",
                    string.IsNullOrWhiteSpace(extractedTitle)
                        ? "Unknown Title"
                        : extractedTitle);

                cmd.Parameters.AddWithValue(
                    "core",
                    coreSkillsList.ToArray());

                cmd.Parameters.AddWithValue(
                    "sec",
                    secondarySkillsList.ToArray());

                cmd.Parameters.AddWithValue(
                    "exp",
                    string.IsNullOrWhiteSpace(extractedExp)
                        ? "Not Specified"
                        : extractedExp);

                cmd.Parameters.AddWithValue("raw", fileContents);

                await cmd.ExecuteNonQueryAsync();

                // 11. Rename the file to mark it as processed instead of deleting it
                string processedFilePath = Path.ChangeExtension(file, ".txt.done");

                // If a .done version already exists from a previous run, remove it first to avoid crashing
                if (File.Exists(processedFilePath))
                {
                    File.Delete(processedFilePath);
                }

                File.Move(file, processedFilePath);

                // 12. Delete the file only after the database insert succeeds
                File.Delete(file);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(
                    $"[✓] Processed and saved: {extractedTitle}\n");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(
                    $"[X] Failure processing {Path.GetFileName(file)}: {ex.Message}\n");
                Console.ResetColor();

                // Keep the file so that the problem can be investigated
                // and the document can be retried.
            }
        }

        Console.WriteLine("Batch process run completed.");
    }

    private static string GetRequiredString(
        JsonElement root,
        string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement element) ||
            element.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                $"AI JSON is missing the string property '{propertyName}'.");
        }

        return element.GetString() ?? string.Empty;
    }

    private static List<string> GetStringArray(
        JsonElement root,
        string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement element) ||
            element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"AI JSON is missing the array property '{propertyName}'.");
        }

        var values = new List<string>();

        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException(
                    $"Property '{propertyName}' must contain only strings.");
            }

            values.Add(item.GetString() ?? string.Empty);
        }

        return values;
    }
}