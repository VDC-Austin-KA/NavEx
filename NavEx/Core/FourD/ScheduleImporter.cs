using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace NavEx.FourD
{
    /// <summary>
    /// Reads a schedule out of whatever the planner exported.
    ///
    /// Four readers cover essentially everything a P6 or MS Project user can
    /// produce without extra tooling: delimited text (the universal fallback),
    /// P6's native XER, P6 XML, and MS Project XML. They all converge on the same
    /// column model, so auto-detection, the mapping dialog and the matcher only
    /// ever deal with one shape.
    ///
    /// Auto-detection runs in two passes. Headers are matched against an alias
    /// table first; then any still-unmapped column is inspected by value shape,
    /// which is what rescues headerless exports and P6's internal column names.
    /// Anything still unresolved is left for the user rather than guessed at —
    /// a wrongly guessed date column silently corrupts an entire 4D sequence.
    /// </summary>
    public static class ScheduleImporter
    {
        public static ScheduleImportResult Import(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("Schedule file not found.", path);

            string extension = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            ScheduleImportResult result;

            switch (extension)
            {
                case ".xer":
                    result = ImportXer(path);
                    break;
                case ".xml":
                    result = ImportXml(path);
                    break;
                default:
                    result = ImportDelimited(path);
                    break;
            }

            result.SourcePath = path;
            AutoDetect(result);
            Rebuild(result);
            return result;
        }

        // ── Delimited text ───────────────────────────────────────────────────

        private static ScheduleImportResult ImportDelimited(string path)
        {
            var result = new ScheduleImportResult { FormatName = "Delimited text" };

            string text = File.ReadAllText(path, DetectEncoding(path));
            char delimiter = SniffDelimiter(text);
            result.FormatName = delimiter == '\t' ? "Tab-separated" : delimiter == ';' ? "Semicolon-separated" : "CSV";

            List<string[]> rows = ParseDelimited(text, delimiter);
            if (rows.Count == 0)
            {
                result.Warnings.Add("The file is empty.");
                return result;
            }

            // A header row is one where few cells parse as dates or numbers.
            string[] first = rows[0];
            bool hasHeader = LooksLikeHeader(first);

            int columnCount = rows.Max(r => r.Length);
            for (int i = 0; i < columnCount; i++)
            {
                result.Columns.Add(new ScheduleColumn
                {
                    Index = i,
                    Header = hasHeader && i < first.Length ? first[i].Trim() : "Column " + (i + 1)
                });
            }

            foreach (string[] row in rows.Skip(hasHeader ? 1 : 0))
                result.Rows.Add(row);

            if (!hasHeader)
                result.Warnings.Add("No header row detected — columns are named by position.");

            return result;
        }

        /// <summary>
        /// RFC 4180 style: doubled quotes escape a quote, quoted fields may hold
        /// the delimiter and newlines.
        /// </summary>
        public static List<string[]> ParseDelimited(string text, char delimiter)
        {
            var rows = new List<string[]>();
            var fields = new List<string>();
            var field = new StringBuilder();
            bool quoted = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                        else quoted = false;
                    }
                    else field.Append(c);
                    continue;
                }

                if (c == '"') { quoted = true; continue; }

                if (c == delimiter)
                {
                    fields.Add(field.ToString());
                    field.Length = 0;
                    continue;
                }

                if (c == '\r') continue;

                if (c == '\n')
                {
                    fields.Add(field.ToString());
                    field.Length = 0;
                    if (fields.Any(f => f.Trim().Length > 0)) rows.Add(fields.ToArray());
                    fields.Clear();
                    continue;
                }

                field.Append(c);
            }

            if (field.Length > 0 || fields.Count > 0)
            {
                fields.Add(field.ToString());
                if (fields.Any(f => f.Trim().Length > 0)) rows.Add(fields.ToArray());
            }

            return rows;
        }

        private static char SniffDelimiter(string text)
        {
            string sample = text.Length > 8000 ? text.Substring(0, 8000) : text;
            string[] lines = sample.Split('\n').Where(l => l.Trim().Length > 0).Take(20).ToArray();
            if (lines.Length == 0) return ',';

            char best = ',';
            double bestScore = -1;

            // The right delimiter is the one that yields the most consistent
            // column count across lines, not simply the most frequent character.
            foreach (char candidate in new[] { ',', '\t', ';', '|' })
            {
                int[] counts = lines.Select(l => l.Count(c => c == candidate)).ToArray();
                if (counts.Max() == 0) continue;

                double mean = counts.Average();
                double variance = counts.Select(c => (c - mean) * (c - mean)).Average();
                double score = mean - variance;
                if (score > bestScore) { bestScore = score; best = candidate; }
            }

            return best;
        }

        private static bool LooksLikeHeader(string[] row)
        {
            if (row == null || row.Length == 0) return false;

            int parseable = 0;
            int named = 0;
            foreach (string cell in row)
            {
                string value = (cell ?? "").Trim();
                if (value.Length == 0) continue;

                DateTime date;
                double number;
                if (TryParseDate(value, out date) ||
                    double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out number))
                    parseable++;

                if (ColumnAliases.FromHeader(value) != ScheduleField.Ignore) named++;
            }

            return named >= 2 || parseable == 0;
        }

        private static Encoding DetectEncoding(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                var bom = new byte[4];
                int read = stream.Read(bom, 0, 4);
                if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) return Encoding.UTF8;
                if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) return Encoding.Unicode;
                if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF) return Encoding.BigEndianUnicode;
            }
            return Encoding.UTF8;
        }

        // ── Primavera XER ────────────────────────────────────────────────────

        /// <summary>
        /// XER is a tab-delimited dump of P6's tables. Each table opens with
        /// <c>%T</c>, its column names follow on <c>%F</c>, and rows are <c>%R</c>.
        /// Only the TASK table matters here.
        /// </summary>
        private static ScheduleImportResult ImportXer(string path)
        {
            var result = new ScheduleImportResult { FormatName = "Primavera P6 XER" };

            string[] lines = File.ReadAllLines(path, XerEncoding());
            string currentTable = null;
            string[] headers = null;

            foreach (string line in lines)
            {
                if (line.Length < 2) continue;
                string[] parts = line.Split('\t');

                switch (parts[0])
                {
                    case "%T":
                        currentTable = parts.Length > 1 ? parts[1].Trim() : null;
                        headers = null;
                        break;

                    case "%F":
                        if (string.Equals(currentTable, "TASK", StringComparison.OrdinalIgnoreCase))
                        {
                            headers = parts.Skip(1).Select(h => h.Trim()).ToArray();
                            for (int i = 0; i < headers.Length; i++)
                                result.Columns.Add(new ScheduleColumn { Index = i, Header = headers[i] });
                        }
                        break;

                    case "%R":
                        if (headers != null && string.Equals(currentTable, "TASK", StringComparison.OrdinalIgnoreCase))
                            result.Rows.Add(parts.Skip(1).ToArray());
                        break;
                }
            }

            if (result.Columns.Count == 0)
                result.Warnings.Add("No TASK table found in the XER file.");

            return result;
        }

        /// <summary>
        /// P6 writes XER as Windows-1252. That code page is built in on .NET
        /// Framework but needs a registered provider on .NET Core, so fall back
        /// rather than throw — the difference only affects accented characters in
        /// task names, which is not worth failing an import over.
        /// </summary>
        private static Encoding XerEncoding()
        {
            try { return Encoding.GetEncoding(1252); }
            catch (Exception) { return Encoding.UTF8; }
        }

        // ── XML: P6 or MS Project ────────────────────────────────────────────

        private static ScheduleImportResult ImportXml(string path)
        {
            XDocument document = XDocument.Load(path);
            XElement root = document.Root;
            if (root == null) throw new InvalidDataException("The XML file has no root element.");

            string localName = root.Name.LocalName;
            string ns = root.Name.NamespaceName ?? "";

            if (localName.IndexOf("Project", StringComparison.OrdinalIgnoreCase) >= 0 &&
                ns.IndexOf("microsoft", StringComparison.OrdinalIgnoreCase) >= 0)
                return ImportMsProjectXml(document);

            if (localName.IndexOf("APIBusinessObjects", StringComparison.OrdinalIgnoreCase) >= 0 ||
                document.Descendants().Any(e => e.Name.LocalName == "Activity"))
                return ImportPrimaveraXml(document);

            // Unknown schema: flatten whatever repeats most and let the user map it.
            return ImportGenericXml(document);
        }

        private static ScheduleImportResult ImportMsProjectXml(XDocument document)
        {
            var result = new ScheduleImportResult { FormatName = "MS Project XML" };
            List<XElement> tasks = document.Descendants()
                .Where(e => e.Name.LocalName == "Task")
                .ToList();
            BuildFromElements(result, tasks);
            if (tasks.Count == 0) result.Warnings.Add("No <Task> elements found.");
            return result;
        }

        private static ScheduleImportResult ImportPrimaveraXml(XDocument document)
        {
            var result = new ScheduleImportResult { FormatName = "Primavera P6 XML" };
            List<XElement> activities = document.Descendants()
                .Where(e => e.Name.LocalName == "Activity")
                .ToList();
            BuildFromElements(result, activities);
            if (activities.Count == 0) result.Warnings.Add("No <Activity> elements found.");
            return result;
        }

        private static ScheduleImportResult ImportGenericXml(XDocument document)
        {
            var result = new ScheduleImportResult { FormatName = "XML" };

            // The element name that repeats most and has children is almost always
            // the record type.
            var candidates = document.Descendants()
                .Where(e => e.HasElements)
                .GroupBy(e => e.Name.LocalName)
                .OrderByDescending(g => g.Count())
                .ToList();

            if (candidates.Count == 0)
            {
                result.Warnings.Add("Could not find repeating records in this XML.");
                return result;
            }

            BuildFromElements(result, candidates[0].ToList());
            result.Warnings.Add("Unrecognised XML schema — treating <" + candidates[0].Key + "> as the task record.");
            return result;
        }

        /// <summary>Flattens each record's immediate child elements into columns.</summary>
        private static void BuildFromElements(ScheduleImportResult result, List<XElement> records)
        {
            var headerIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (XElement record in records)
            {
                foreach (XElement child in record.Elements())
                {
                    if (child.HasElements) continue;
                    if (headerIndex.ContainsKey(child.Name.LocalName)) continue;

                    headerIndex[child.Name.LocalName] = result.Columns.Count;
                    result.Columns.Add(new ScheduleColumn
                    {
                        Index = result.Columns.Count,
                        Header = child.Name.LocalName
                    });
                }
            }

            foreach (XElement record in records)
            {
                var row = new string[result.Columns.Count];
                foreach (XElement child in record.Elements())
                {
                    int index;
                    if (child.HasElements) continue;
                    if (headerIndex.TryGetValue(child.Name.LocalName, out index))
                        row[index] = child.Value;
                }
                result.Rows.Add(row);
            }
        }

        // ── Column detection ─────────────────────────────────────────────────

        public static void AutoDetect(ScheduleImportResult result)
        {
            foreach (ScheduleColumn column in result.Columns)
            {
                column.Samples.Clear();
                foreach (string[] row in result.Rows.Take(50))
                {
                    if (column.Index >= row.Length) continue;
                    string value = (row[column.Index] ?? "").Trim();
                    if (value.Length > 0) column.Samples.Add(value);
                }
            }

            // Pass 1 — header aliases. Highest-confidence signal, so it wins.
            var claimed = new HashSet<ScheduleField>();
            foreach (ScheduleColumn column in result.Columns)
            {
                ScheduleField field = ColumnAliases.FromHeader(column.Header);
                if (field == ScheduleField.Ignore || claimed.Contains(field)) continue;

                column.Field = field;
                column.AutoDetected = true;
                column.DetectionBasis = "header '" + column.Header + "'";
                claimed.Add(field);
            }

            // Pass 2 — value shapes, for anything still missing.
            var dateColumns = result.Columns
                .Where(c => c.Field == ScheduleField.Ignore && LooksLikeDates(c))
                .ToList();

            if (!claimed.Contains(ScheduleField.PlannedStart) && dateColumns.Count > 0)
            {
                Assign(dateColumns[0], ScheduleField.PlannedStart, "column parses as dates", claimed);
                dateColumns.RemoveAt(0);
            }
            if (!claimed.Contains(ScheduleField.PlannedFinish) && dateColumns.Count > 0)
            {
                Assign(dateColumns[0], ScheduleField.PlannedFinish, "column parses as dates", claimed);
                dateColumns.RemoveAt(0);
            }

            if (!claimed.Contains(ScheduleField.Name))
            {
                // The name column is the free-text one with the most distinct,
                // reasonably long values.
                ScheduleColumn best = result.Columns
                    .Where(c => c.Field == ScheduleField.Ignore && c.Samples.Count > 0)
                    .Where(c => !LooksLikeDates(c) && !LooksNumeric(c))
                    .OrderByDescending(c => c.Samples.Distinct(StringComparer.OrdinalIgnoreCase).Count())
                    .ThenByDescending(c => c.Samples.Average(s => (double)s.Length))
                    .FirstOrDefault();

                if (best != null)
                    Assign(best, ScheduleField.Name, "longest distinct free text", claimed);
            }

            // Order the two planned dates correctly if they came out reversed.
            ScheduleColumn start = result.Columns.FirstOrDefault(c => c.Field == ScheduleField.PlannedStart);
            ScheduleColumn finish = result.Columns.FirstOrDefault(c => c.Field == ScheduleField.PlannedFinish);
            if (start != null && finish != null && MedianDate(start) > MedianDate(finish))
            {
                start.Field = ScheduleField.PlannedFinish;
                finish.Field = ScheduleField.PlannedStart;
                start.DetectionBasis += " (swapped: later dates)";
                finish.DetectionBasis += " (swapped: earlier dates)";
            }
        }

        private static void Assign(ScheduleColumn column, ScheduleField field, string basis, HashSet<ScheduleField> claimed)
        {
            column.Field = field;
            column.AutoDetected = true;
            column.DetectionBasis = basis;
            claimed.Add(field);
        }

        private static bool LooksLikeDates(ScheduleColumn column)
        {
            if (column.Samples.Count == 0) return false;
            int parsed = column.Samples.Count(s => { DateTime d; return TryParseDate(s, out d); });
            return parsed >= Math.Max(1, column.Samples.Count * 0.7);
        }

        private static bool LooksNumeric(ScheduleColumn column)
        {
            if (column.Samples.Count == 0) return false;
            int parsed = column.Samples.Count(s =>
            {
                double d;
                return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out d);
            });
            return parsed >= column.Samples.Count * 0.8;
        }

        private static DateTime MedianDate(ScheduleColumn column)
        {
            var dates = new List<DateTime>();
            foreach (string sample in column.Samples)
            {
                DateTime date;
                if (TryParseDate(sample, out date)) dates.Add(date);
            }
            if (dates.Count == 0) return DateTime.MinValue;
            dates.Sort();
            return dates[dates.Count / 2];
        }

        // ── Row -> task ──────────────────────────────────────────────────────

        /// <summary>Re-reads every row through the current column mapping.</summary>
        public static void Rebuild(ScheduleImportResult result)
        {
            result.Tasks.Clear();

            var byField = new Dictionary<ScheduleField, int>();
            foreach (ScheduleColumn column in result.Columns)
                if (column.Field != ScheduleField.Ignore && !byField.ContainsKey(column.Field))
                    byField[column.Field] = column.Index;

            int rowNumber = 0;
            int skipped = 0;

            foreach (string[] row in result.Rows)
            {
                rowNumber++;
                var task = new ScheduleTask { SourceRow = rowNumber };

                foreach (ScheduleColumn column in result.Columns)
                    if (column.Index < row.Length)
                        task.Raw[column.Header] = row[column.Index] ?? "";

                task.TaskId = Get(row, byField, ScheduleField.TaskId);
                task.Name = Get(row, byField, ScheduleField.Name);
                task.Description = Get(row, byField, ScheduleField.Description);
                task.TaskType = Get(row, byField, ScheduleField.TaskType);
                task.Wbs = Get(row, byField, ScheduleField.Wbs);

                task.PlannedStart = GetDate(row, byField, ScheduleField.PlannedStart);
                task.PlannedFinish = GetDate(row, byField, ScheduleField.PlannedFinish);
                task.ActualStart = GetDate(row, byField, ScheduleField.ActualStart);
                task.ActualFinish = GetDate(row, byField, ScheduleField.ActualFinish);

                string duration = Get(row, byField, ScheduleField.Duration);
                double days;
                if (!string.IsNullOrWhiteSpace(duration) && TryParseDuration(duration, out days))
                    task.DurationDays = days;

                // A duration can stand in for a missing finish date.
                if (task.PlannedStart.HasValue && !task.PlannedFinish.HasValue && task.DurationDays.HasValue)
                    task.PlannedFinish = task.PlannedStart.Value.AddDays(task.DurationDays.Value);

                if (string.IsNullOrWhiteSpace(task.Name)) { skipped++; continue; }

                result.Tasks.Add(task);
            }

            if (skipped > 0)
                result.Warnings.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0:N0} row(s) had no task name and were skipped.", skipped));

            int undated = result.Tasks.Count(t => !t.PlannedStart.HasValue || !t.PlannedFinish.HasValue);
            if (undated > 0)
                result.Warnings.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0:N0} task(s) are missing planned dates and cannot be sent to TimeLiner.", undated));
        }

        private static string Get(string[] row, Dictionary<ScheduleField, int> byField, ScheduleField field)
        {
            int index;
            if (!byField.TryGetValue(field, out index)) return "";
            if (index >= row.Length) return "";
            return (row[index] ?? "").Trim();
        }

        private static DateTime? GetDate(string[] row, Dictionary<ScheduleField, int> byField, ScheduleField field)
        {
            string value = Get(row, byField, field);
            if (string.IsNullOrWhiteSpace(value)) return null;
            DateTime date;
            return TryParseDate(value, out date) ? date : (DateTime?)null;
        }

        /// <summary>
        /// Accepts the formats these exports actually use, invariant first so a
        /// machine on a dd/MM locale still reads an ISO or US export correctly.
        /// </summary>
        public static bool TryParseDate(string value, out DateTime date)
        {
            date = default(DateTime);
            if (string.IsNullOrWhiteSpace(value)) return false;

            string trimmed = value.Trim();

            // P6 marks actualised dates with a trailing "A" and constraint dates
            // with a "*"; strip that decoration before parsing.
            trimmed = trimmed.TrimEnd('A', 'a', '*', ' ');

            string[] formats =
            {
                "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.fff",
                "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss",
                "MM/dd/yyyy", "M/d/yyyy", "MM/dd/yyyy HH:mm", "M/d/yyyy H:mm",
                "dd/MM/yyyy", "d/M/yyyy", "dd/MM/yyyy HH:mm",
                "dd-MMM-yy", "dd-MMM-yyyy", "d-MMM-yy", "MMM d, yyyy", "d MMM yyyy",
                "yyyyMMdd"
            };

            if (DateTime.TryParseExact(trimmed, formats, CultureInfo.InvariantCulture,
                                       DateTimeStyles.AllowWhiteSpaces, out date))
                return true;

            if (DateTime.TryParse(trimmed, CultureInfo.InvariantCulture,
                                  DateTimeStyles.AllowWhiteSpaces, out date))
                return true;

            if (DateTime.TryParse(trimmed, CultureInfo.CurrentCulture,
                                  DateTimeStyles.AllowWhiteSpaces, out date))
                return true;

            return false;
        }

        /// <summary>Durations arrive as "10", "10d", "80h", "10 days".</summary>
        public static bool TryParseDuration(string value, out double days)
        {
            days = 0;
            if (string.IsNullOrWhiteSpace(value)) return false;

            string trimmed = value.Trim().ToLowerInvariant();
            bool hours = trimmed.EndsWith("h") || trimmed.Contains("hour");
            string numeric = new string(trimmed.Where(c => char.IsDigit(c) || c == '.' || c == '-').ToArray());

            double parsed;
            if (!double.TryParse(numeric, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed))
                return false;

            // P6 stores durations in hours; an 8-hour day is the standard assumption.
            days = hours ? parsed / 8.0 : parsed;
            return true;
        }
    }
}
