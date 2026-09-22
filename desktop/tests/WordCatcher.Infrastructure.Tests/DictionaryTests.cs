using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using WordCatcher.Core.Enums;
using WordCatcher.Core.Models;
using WordCatcher.Infrastructure.Dictionary;
using WordCatcher.Infrastructure.Dictionary.Models;
using Xunit;

namespace WordCatcher.Infrastructure.Tests;

public class DictionaryTests : IDisposable
{
    private readonly string _tempDir;

    public DictionaryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"dict_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
                // ignore
            }
        }
    }

    [Fact]
    public void FormatEntry_PrefersUsPronunciationAndKeepsRareSensesForExpansion()
    {
        var entry = new DistributionEntryV5
        {
            Headword = "apple",
            SchemaVersion = "distribution_entry_v5",
            EntryId = "e_apple",
            Pronunciations = new DictionaryPronunciations
            {
                Us = "æpəl",
                Uk = "ˈæp.əl",
                Ipa = "æp.l"
            },
            MemoryHook = "An apple a day",
            PosGroups = new List<DictionaryPosGroupV5>
            {
                new()
                {
                    Pos = "noun",
                    Meanings = new List<DictionaryMeaningV5>
                    {
                        new() { SenseId = "s1", Priority = "core", LearnerExplanation = "一种常见的水果" },
                        new() { SenseId = "s2", Priority = "rare", LearnerExplanation = "极其生僻的含义" },
                        new() { SenseId = "s3", Priority = "common", LearnerExplanation = "苹果树" }
                    }
                }
            }
        };

        var result = OfflineDictionaryService.FormatEntry(entry);

        Assert.Equal("apple", result.Word);
        Assert.Equal("/æpəl/", result.Reading);
        Assert.Equal("n.", result.PartOfSpeech);
        Assert.Contains("一种常见的水果", result.Definition);
        Assert.Contains("苹果树", result.Definition);
        Assert.DoesNotContain("极其生僻的含义", result.Definition);
        Assert.Equal("An apple a day", result.MemoryHook);
        Assert.Equal(LookupSource.OpenDictionary, result.Source);
        Assert.Equal(new[] { "core", "common", "rare" }, result.PosGroups![0].Meanings.Select(m => m.Priority));
    }

    [Fact]
    public void FormatEntry_FallsBackToRareMeanings_WhenNoCoreOrCommonMeaningsExist()
    {
        var entry = new DistributionEntryV5
        {
            Headword = "rareword",
            PosGroups = new List<DictionaryPosGroupV5>
            {
                new()
                {
                    Pos = "noun",
                    Meanings = new List<DictionaryMeaningV5>
                    {
                        new() { SenseId = "s1", Priority = "rare", LearnerExplanation = "罕见古英语专业释义" }
                    }
                }
            }
        };

        var result = OfflineDictionaryService.FormatEntry(entry);

        Assert.Equal("rareword", result.Word);
        Assert.Contains("罕见古英语专业释义", result.Definition);
    }

    [Theory]
    [InlineData("kwɪk", "/kwɪk/")]
    [InlineData("/kwɪk", "/kwɪk/")]
    [InlineData("kwɪk/", "/kwɪk/")]
    [InlineData("/kwɪk/", "/kwɪk/")]
    public void FormatEntry_NormalizesOneSidedPronunciationDelimiters(string raw, string expected)
    {
        var entry = new DistributionEntryV5
        {
            Headword = "quick",
            Pronunciations = new DictionaryPronunciations { Ipa = raw },
            PosGroups = new List<DictionaryPosGroupV5>()
        };

        Assert.Equal(expected, OfflineDictionaryService.FormatEntry(entry).Reading);
    }

    [Fact]
    public void OfficialV5EntryShape_DeserializesLanguagePronunciationFormsAndProperName()
    {
        const string json = """
        {
          "definition_language": {"code": "zh-Hans", "name": "Chinese (Simplified)"},
          "entry_id": "entry_quick",
          "headword": "quick",
          "headword_language": {"code": "en", "name": "English"},
          "headword_summary": "快速的",
          "memory_hook": "迅速行动",
          "normalized_headword": "quick",
          "pos_groups": [
            {
              "forms": [
                {"roman": null, "tags": ["comparative"], "text": "quicker"}
              ],
              "meanings": [
                {
                  "examples": [
                    {"text": "a quick response", "translation": "快速回应"}
                  ],
                  "learner_explanation": "速度快的",
                  "priority": "core",
                  "sense_id": "s1",
                  "short_gloss": "快"
                }
              ],
              "pos": "adjective",
              "proper_name": false,
              "pronunciations": [
                {"ipa": "/kwɪk/", "tags": [], "text": null}
              ],
              "summary": "速度快"
            }
          ]
        }
        """;

        var entry = JsonSerializer.Deserialize<DistributionEntryV5>(json);

        Assert.NotNull(entry);
        Assert.Equal("en", entry!.HeadwordLanguage?.Code);
        Assert.Equal("zh-Hans", entry.DefinitionLanguage?.Code);
        Assert.Equal("quicker", entry.PosGroups[0].Forms![0].Text);
        Assert.True(entry.PosGroups[0].ProperName == false);

        var result = OfflineDictionaryService.FormatEntry(entry);

        Assert.Equal("quick", result.Word);
        Assert.Equal("/kwɪk/", result.Reading);
        Assert.Equal("adj. 快", result.Definition);
        Assert.Equal("速度快的", result.PosGroups![0].Meanings[0].LearnerExplanation);
    }

    [Fact]
    public void RealDigitalEntry_KeepsEverySenseAndShowsCoreSensesBeforeFinger()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "digital.v5.json"));
        var entry = JsonSerializer.Deserialize<DistributionEntryV5>(json)!;
        var result = OfflineDictionaryService.FormatEntry(entry);
        Assert.Equal(2, result.PosGroups!.Count);
        var adjective = result.PosGroups[0];
        var noun = result.PosGroups[1];
        Assert.Equal(new[] { "s2", "s3", "s1" }, adjective.Meanings.Select(m => m.SenseId));
        Assert.Equal("数字设备或数字技术", noun.Meanings[0].ShortGloss);
        Assert.Equal(9, result.PosGroups.Sum(g => g.Meanings.Count));
        Assert.Equal(2, noun.Meanings.Count(m => m.Priority == "rare"));
        Assert.Contains("与计算机和信息时代有关的", adjective.QuickSummary);
        Assert.Contains("数字设备或数字技术", noun.QuickSummary);
        Assert.Contains("n. 数字设备或数字技术", result.Definition);
        Assert.DoesNotContain("钢琴等键盘乐器的琴键", result.Definition);
        Assert.Contains(noun.Meanings, m => m.ShortGloss == "钢琴等键盘乐器的琴键");
        Assert.All(result.PosGroups.SelectMany(g => g.Meanings), m => Assert.False(string.IsNullOrWhiteSpace(m.LearnerExplanation)));
    }

    [Fact]
    public void RareOnlyEntryAndMissingGlossStillHaveReadableSummary()
    {
        var result = OfflineDictionaryService.FormatEntry(new DistributionEntryV5
        {
            Headword = "rareword",
            PosGroups = [new() { Pos = "noun", Meanings = [new() { Priority = "rare", LearnerExplanation = "保留少见义的完整解释" }] }]
        });
        Assert.Equal("保留少见义的完整解释", result.PosGroups![0].QuickSummary);
        Assert.Equal("n. 保留少见义的完整解释", result.Definition);
    }

    [Fact]
    public async Task InstallFromFileAsync_And_LookupAsync_WithLemmatization()
    {
        // 1. Build a fixture SQLite file
        var fixtureSqlite = Path.Combine(_tempDir, "fixture.sqlite");
        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = fixtureSqlite,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        await using (var conn = new SqliteConnection(connStr))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE metadata (key TEXT PRIMARY KEY, value_json TEXT NOT NULL);
CREATE TABLE entries (id TEXT PRIMARY KEY, headword_language_code TEXT NOT NULL, normalized_headword TEXT NOT NULL, document_json TEXT NOT NULL);
CREATE TABLE pos_groups (id TEXT PRIMARY KEY);
CREATE TABLE meanings (id TEXT PRIMARY KEY);
CREATE TABLE meaning_examples (id TEXT PRIMARY KEY);

INSERT INTO metadata (key, value_json) VALUES
('distribution_schema_version', '""distribution_entry_v5""'),
('sqlite_schema_version', '""distribution_sqlite_v1""'),
('headword_language', '{""code"":""en"",""name"":""English""}'),
('definition_language', '{""code"":""zh-Hans"",""name"":""Chinese (Simplified)""}');
";
            await cmd.ExecuteNonQueryAsync();

            var catEntry = new DistributionEntryV5
            {
                Headword = "cat",
                SchemaVersion = "distribution_entry_v5",
                EntryId = "entry_cat",
                Pronunciations = new DictionaryPronunciations { Us = "kæt" },
                PosGroups = new List<DictionaryPosGroupV5>
                {
                    new()
                    {
                        Pos = "noun",
                        Meanings = new List<DictionaryMeaningV5>
                        {
                            new() { SenseId = "cat_1", Priority = "core", LearnerExplanation = "猫科动物，家猫" }
                        }
                    }
                }
            };

            await using var insertCmd = conn.CreateCommand();
            insertCmd.CommandText = @"
INSERT INTO entries (id, headword_language_code, normalized_headword, document_json)
VALUES ($id, $lang, $norm, $json);";
            insertCmd.Parameters.AddWithValue("$id", "1");
            insertCmd.Parameters.AddWithValue("$lang", "en");
            insertCmd.Parameters.AddWithValue("$norm", "cat");
            insertCmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(catEntry));
            await insertCmd.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        // 2. Compress to .sqlite.gz
        var fixtureGz = Path.Combine(_tempDir, "fixture.sqlite.gz");
        await using (var rawFile = File.OpenRead(fixtureSqlite))
        await using (var gzFile = File.Create(fixtureGz))
        await using (var gzStream = new GZipStream(gzFile, CompressionMode.Compress))
        {
            await rawFile.CopyToAsync(gzStream);
        }

        // 3. Install
        var installDir = Path.Combine(_tempDir, "installed");
        Directory.CreateDirectory(installDir);

        using var httpClient = new System.Net.Http.HttpClient();
        var installer = new DictionaryInstaller(httpClient, installDir);
        var progressList = new List<DictionaryInstallProgress>();
        var progress = new Progress<DictionaryInstallProgress>(p => progressList.Add(p));

        await installer.InstallFromFileAsync(fixtureGz, progress);

        Assert.True(File.Exists(installer.TargetSqlitePath));
        Assert.True(File.Exists(installer.MetaJsonPath));

        // 4. Query using OfflineDictionaryService
        using var service = new OfflineDictionaryService(installDir);
        Assert.True(service.IsInstalled);
        Assert.Equal("en", service.Metadata?.HeadwordLanguage);

        // Exact query
        var catRes = await service.LookupAsync("cat");
        Assert.NotNull(catRes);
        Assert.Equal("cat", catRes.Word);
        Assert.Equal("/kæt/", catRes.Reading);
        Assert.Contains("猫科动物，家猫", catRes.Definition);

        // Inflected form query ("cats" should hit "cat" via Lemmatizer)
        var catsRes = await service.LookupAsync("cats");
        Assert.NotNull(catsRes);
        Assert.Equal("cat", catsRes.Word);

        // Long text should be skipped (returns null)
        var longTextRes = await service.LookupAsync("this is a long sentence");
        Assert.Null(longTextRes);

        // 5. Verify that even if file extension is .download, GZip is correctly detected and decompressed
        var downloadExtensionFile = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.download");
        File.Copy(fixtureGz, downloadExtensionFile);

        var installDir2 = Path.Combine(_tempDir, "installed_from_download");
        Directory.CreateDirectory(installDir2);
        var installer2 = new DictionaryInstaller(httpClient, installDir2);
        await installer2.InstallFromFileAsync(downloadExtensionFile, new Progress<DictionaryInstallProgress>());

        using var service2 = new OfflineDictionaryService(installDir2);
        Assert.True(service2.IsInstalled);
        var resCat = await service2.LookupAsync("cat");
        Assert.NotNull(resCat);
        Assert.Equal("cat", resCat.Word);
    }
}
