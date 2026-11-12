using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Utils;

namespace SmartUnrealLocalization.Services
{
    /// <summary>Which feature sent a prompt. The names are identifiers: they appear in
    /// file names and in the history list as they are, in every interface language.</summary>
    public enum PromptMode { SingleTranslation, Multitranslation, AIQC }

    /// <summary>One remembered prompt: the instructions a run sent to the model.</summary>
    public sealed class PromptRecord
    {
        public int Number { get; set; }
        /// <summary>Name of the file the run worked on (no directory).</summary>
        public string File { get; set; } = "";
        public PromptMode Mode { get; set; }
        /// <summary>Local time of the first run that used this prompt.</summary>
        public DateTime Time { get; set; }
        public string Text { get; set; } = "";

        /// <summary>The list title: "1_Game.locres_11/10/2026_17:26_SingleTranslation".</summary>
        [JsonIgnore]
        public string DisplayName => string.Format(CultureInfo.InvariantCulture,
            "{0}_{1}_{2:MM'/'dd'/'yyyy}_{2:HH':'mm}_{3}", Number, File, Time, Mode);
    }

    /// <summary>
    /// Prompt history: every distinct prompt the app sent, one JSON file each, under
    /// <c>prompts/</c> in the profile folder — <c>single/</c>, <c>multi/</c> and
    /// <c>qc/</c> keep the three features apart.
    ///
    /// <para>A prompt is the INSTRUCTION text (the system prompt with the user's
    /// additional instructions) — not the rows, which travel in separate messages
    /// and would copy the whole file into the history on every run.</para>
    ///
    /// <para>Nothing is stored twice: a run that sends the same text, in the same
    /// mode, for the same file as an existing record adds nothing.</para>
    /// </summary>
    public static class PromptHistory
    {
        private static readonly object Gate = new();

        /// <summary>Raised (on the caller's thread) after a record is added or the history is cleared.</summary>
        public static event Action? Changed;

        public static string RootDir => Path.Combine(AppConfig.ConfigDir(), "prompts");

        public static string DirFor(PromptMode mode) => Path.Combine(RootDir, mode switch
        {
            PromptMode.SingleTranslation => "single",
            PromptMode.Multitranslation => "multi",
            _ => "qc",
        });

        /// <summary>
        /// Remember a prompt. Returns the new record, or null when the text is empty
        /// or the same prompt is already stored. Never throws: history is a
        /// convenience and must not fail the run that feeds it.
        /// </summary>
        public static PromptRecord? Record(PromptMode mode, string? fileName, string? text, DateTime? now = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string file = string.IsNullOrWhiteSpace(fileName) ? "-" : Path.GetFileName(fileName);
            PromptRecord record;
            try
            {
                lock (Gate)
                {
                    var all = LoadAll();
                    if (all.Any(r => r.Mode == mode
                                     && string.Equals(r.File, file, StringComparison.OrdinalIgnoreCase)
                                     && string.Equals(r.Text, text, StringComparison.Ordinal)))
                        return null;

                    record = new PromptRecord
                    {
                        Number = all.Count == 0 ? 1 : all.Max(r => r.Number) + 1,
                        File = file,
                        Mode = mode,
                        Time = now ?? DateTime.Now,
                        Text = text,
                    };
                    string dir = DirFor(mode);
                    Directory.CreateDirectory(dir);
                    string name = string.Format(CultureInfo.InvariantCulture, "{0:0000}_{1}_{2:yyyy-MM-dd_HH-mm}_{3}.json",
                        record.Number, SafeName(file), record.Time, mode);
                    AtomicFile.WriteAllText(Path.Combine(dir, name), JsonConvert.SerializeObject(record, Formatting.Indented), new System.Text.UTF8Encoding(false));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[prompts] not saved: {ex.Message}");
                return null;
            }
            Changed?.Invoke();
            return record;
        }

        /// <summary>Every stored prompt, newest first. Unreadable files are skipped.</summary>
        public static List<PromptRecord> LoadAll()
        {
            var list = new List<PromptRecord>();
            if (!Directory.Exists(RootDir)) return list;
            foreach (var path in Directory.EnumerateFiles(RootDir, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    var r = JsonConvert.DeserializeObject<PromptRecord>(System.IO.File.ReadAllText(path));
                    if (r != null && !string.IsNullOrEmpty(r.Text)) list.Add(r);
                }
                catch (Exception ex) { Console.WriteLine($"[prompts] unreadable {Path.GetFileName(path)}: {ex.Message}"); }
            }
            return list.OrderByDescending(r => r.Number).ToList();
        }

        /// <summary>Delete the whole history.</summary>
        public static void Clear()
        {
            lock (Gate)
            {
                if (Directory.Exists(RootDir)) Directory.Delete(RootDir, recursive: true);
            }
            Changed?.Invoke();
        }

        private static string SafeName(string file)
        {
            var bad = Path.GetInvalidFileNameChars();
            var chars = file.Select(c => Array.IndexOf(bad, c) >= 0 ? '-' : c).ToArray();
            string s = new string(chars);
            return s.Length > 60 ? s.Substring(0, 60) : s;
        }
    }
}
