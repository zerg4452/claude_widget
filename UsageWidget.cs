using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace UsageWidget
{
    public class Limit
    {
        public double UsedPct = -1;
        public long ResetsAt = 0;
        public int WindowMin = 0;

        // Used percent, treating a window whose reset time has passed as fresh.
        public double EffectiveUsed(long now)
        {
            if (UsedPct < 0) return -1;
            if (ResetsAt > 0 && ResetsAt <= now) return 0;
            return Math.Max(0, Math.Min(100, UsedPct));
        }
    }

    public class Usage
    {
        public Limit Short = new Limit();
        public Limit Week = new Limit();
        public DateTime Updated = DateTime.MinValue;
        public bool HasData { get { return Short.UsedPct >= 0 || Week.UsedPct >= 0; } }
    }

    // Output speed, measured per request from the session transcripts.
    public class Tps
    {
        public double Last = -1;
        public long LastTokens = 0;
        public double LastSecs = 0;
        public DateTime LastAt = DateTime.MinValue; // local time the request finished
        public double Avg = -1;                     // over requests finished in the last AvgMinutes
        public int AvgCount = 0;
        public const int AvgMinutes = 10;
        public bool HasData { get { return Last >= 0; } }

        // Latest turn of the main sessions: prompt submit -> final answer, tool time included.
        public DateTime TurnStart = DateTime.MinValue; // local
        public DateTime TurnEnd = DateTime.MinValue;   // local; MinValue while running
        public bool TurnActive { get { return TurnStart != DateTime.MinValue && TurnEnd == DateTime.MinValue; } }
        public bool HasTurn { get { return TurnStart != DateTime.MinValue; } }

        // Per main session, for completion notifications.
        public List<SessionTurn> Sessions = new List<SessionTurn>();
    }

    public class SessionTurn
    {
        public string AI = "Claude";
        public string SessionId, SessionTitle;
        public string Path, Title, Project;
        public DateTime Start, End; // local; End is MinValue while running
        public bool Interrupted, Abandoned;
    }

    public static class Sources
    {
        static double Num(string obj, string key)
        {
            Match m = Regex.Match(obj, "\"" + key + "\"\\s*:\\s*(-?[0-9.]+(?:[eE][-+]?[0-9]+)?)");
            if (!m.Success) return -1;
            double v;
            return double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : -1;
        }

        static string Obj(string text, string key)
        {
            Match m = Regex.Match(text, "\"" + key + "\"\\s*:\\s*\\{([^{}]*)\\}");
            return m.Success ? m.Groups[1].Value : null;
        }

        static Limit Parse(string obj, string pctKey)
        {
            Limit l = new Limit();
            if (obj == null) return l;
            l.UsedPct = Num(obj, pctKey);
            double r = Num(obj, "resets_at");
            l.ResetsAt = r > 0 ? (long)r : 0;
            if (l.ResetsAt == 0)
            {
                // The OAuth usage API sends ISO-8601 strings instead of unix seconds.
                Match m = Regex.Match(obj, "\"resets_at\"\\s*:\\s*\"([^\"]+)\"");
                DateTimeOffset t;
                if (m.Success && DateTimeOffset.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out t))
                    l.ResetsAt = t.ToUnixTimeSeconds();
            }
            double w = Num(obj, "window_minutes");
            l.WindowMin = w > 0 ? (int)w : 0;
            return l;
        }

        static string ReadTail(string path, int maxBytes)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long len = fs.Length;
                long start = Math.Max(0, len - maxBytes);
                fs.Seek(start, SeekOrigin.Begin);
                byte[] buf = new byte[len - start];
                int read = 0;
                while (read < buf.Length)
                {
                    int r = fs.Read(buf, read, buf.Length - read);
                    if (r <= 0) break;
                    read += r;
                }
                return Encoding.UTF8.GetString(buf, 0, read);
            }
        }

        // Written by statusline.ps1 from Claude Code's status line input (rate_limits object).
        public static Usage ReadClaude(string path)
        {
            Usage u = new Usage();
            try
            {
                if (!File.Exists(path)) return u;
                string text = ReadTail(path, 65536);
                u.Short = Parse(Obj(text, "five_hour"), "used_percentage");
                u.Week = Parse(Obj(text, "seven_day"), "used_percentage");
                u.Updated = File.GetLastWriteTime(path);
            }
            catch { }
            return u;
        }

        // Same read-only endpoint Claude Code's /usage uses; no model call, no tokens consumed.
        // Signs in with Claude Code's own OAuth login and only ever sends it to api.anthropic.com.
        // Returns null on any failure so the caller keeps its previous data.
        public static Usage ReadClaudeApi(string credentialsPath)
        {
            try
            {
                if (!File.Exists(credentialsPath)) return null;
                string oauth = Obj(File.ReadAllText(credentialsPath), "claudeAiOauth");
                if (oauth == null) return null;
                Match tok = Regex.Match(oauth, "\"accessToken\"\\s*:\\s*\"([^\"]+)\"");
                if (!tok.Success) return null;
                double exp = Num(oauth, "expiresAt");
                // Expired: wait for Claude Code to refresh it rather than touching the refresh token.
                if (exp > 0 && exp <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) return null;

                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("https://api.anthropic.com/api/oauth/usage");
                req.Method = "GET";
                req.Timeout = 10000;
                req.ReadWriteTimeout = 10000;
                req.UserAgent = "claude-code/usage-widget";
                req.Accept = "application/json";
                req.Headers["Authorization"] = "Bearer " + tok.Groups[1].Value;
                req.Headers["anthropic-beta"] = "oauth-2025-04-20";
                string text;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    text = sr.ReadToEnd();

                Usage u = new Usage();
                u.Short = Parse(Obj(text, "five_hour"), "utilization");
                u.Week = Parse(Obj(text, "seven_day"), "utilization");
                if (!u.HasData) return null;
                u.Updated = DateTime.Now;
                return u;
            }
            catch { return null; }
        }

        static string[] Desc(string[] a)
        {
            Array.Sort(a, StringComparer.Ordinal);
            Array.Reverse(a);
            return a;
        }

        static List<string> RecentFiles(string root, int maxDays)
        {
            List<string> result = new List<string>();
            if (!Directory.Exists(root)) return result;
            int days = 0;
            foreach (string y in Desc(Directory.GetDirectories(root)))
                foreach (string m in Desc(Directory.GetDirectories(y)))
                    foreach (string d in Desc(Directory.GetDirectories(m)))
                    {
                        result.AddRange(Directory.GetFiles(d, "*.jsonl"));
                        if (++days >= maxDays) return result;
                    }
            return result;
        }

        static void Assign(Usage u, Limit l, bool isPrimary)
        {
            if (l.UsedPct < 0) return;
            bool isShort = l.WindowMin > 0 ? l.WindowMin <= 1440 : isPrimary;
            if (isShort) u.Short = l; else u.Week = l;
        }

        // Codex session logs carry rate_limits.primary/secondary on token_count events.
        public static Usage ReadCodex(string sessionsDir)
        {
            Usage u = new Usage();
            try
            {
                List<string> files = RecentFiles(sessionsDir, 4);
                files.Sort(delegate(string a, string b) { return File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)); });
                int n = 0;
                foreach (string f in files)
                {
                    if (n++ >= 8) break;
                    string text = ReadTail(f, 2 * 1024 * 1024);
                    int idx = text.LastIndexOf("\"rate_limits\":{", StringComparison.Ordinal);
                    if (idx < 0) continue;
                    string seg = text.Substring(idx, Math.Min(1500, text.Length - idx));
                    Assign(u, Parse(Obj(seg, "primary"), "used_percent"), true);
                    Assign(u, Parse(Obj(seg, "secondary"), "used_percent"), false);
                    if (u.HasData)
                    {
                        u.Updated = File.GetLastWriteTime(f);
                        break;
                    }
                }
            }
            catch { }
            return u;
        }

        class CodexParsed
        {
            public long Offset, Total = -1;
            public DateTime Mtime, Start, End, Anchor, ResponseEnd, NextAnchor;
            public string TurnId, Path, Title, Project, SessionId;
            public bool Subagent, Interrupted;
            public List<SessionTurn> Completed = new List<SessionTurn>();
            public List<Req> Reqs = new List<Req>();
        }

        static readonly Dictionary<string, CodexParsed> codexCache = new Dictionary<string, CodexParsed>(StringComparer.OrdinalIgnoreCase);
        static readonly System.Web.Script.Serialization.JavaScriptSerializer json = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        static Dictionary<string, object> Map(Dictionary<string, object> obj, string key)
        {
            object value;
            return obj != null && obj.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }

        static string Str(Dictionary<string, object> obj, string key)
        {
            object value;
            return obj != null && obj.TryGetValue(key, out value) ? value as string : null;
        }

        static DateTime CodexTime(string value)
        {
            DateTime t;
            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t) ? t : DateTime.MinValue;
        }

        static DateTime CodexTime(Dictionary<string, object> obj, string key)
        {
            object value;
            if (!obj.TryGetValue(key, out value) || value == null) return DateTime.MinValue;
            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            long seconds;
            if (!long.TryParse(text, out seconds)) return CodexTime(text);
            try { return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime; }
            catch (ArgumentOutOfRangeException) { return DateTime.MinValue; }
        }

        static void CodexLine(CodexParsed p, string line)
        {
            // The envelope, rather than text embedded in a tool result, determines the event type.
            Dictionary<string, object> e = json.DeserializeObject(line) as Dictionary<string, object>;
            Dictionary<string, object> payload = Map(e, "payload");
            if (payload == null) return;
            string kind = Str(e, "type"), type = Str(payload, "type");
            if (kind == "session_meta")
            {
                Dictionary<string, object> source = Map(payload, "source");
                p.Subagent = source != null && source.ContainsKey("subagent");
                p.SessionId = Str(payload, "id");
                string cwd = Str(payload, "cwd");
                if (!string.IsNullOrEmpty(cwd)) p.Project = System.IO.Path.GetFileName(cwd.TrimEnd('\\', '/'));
                return;
            }
            if (p.Subagent) return;
            DateTime ts = CodexTime(Str(e, "timestamp"));
            if (ts == DateTime.MinValue) return;
            if (kind == "event_msg")
            {
                if (type == "task_started")
                {
                    p.Start = CodexTime(payload, "started_at");
                    if (p.Start == DateTime.MinValue) p.Start = ts;
                    p.End = p.ResponseEnd = p.NextAnchor = DateTime.MinValue;
                    p.Anchor = p.Start;
                    p.TurnId = Str(payload, "turn_id");
                    p.Interrupted = false;
                    p.Title = null;
                }
                else if (type == "task_complete" || type == "task_completed" || type == "turn_aborted")
                {
                    string id = Str(payload, "turn_id");
                    if (p.Start != DateTime.MinValue && p.End == DateTime.MinValue && (id == null || p.TurnId == null || id == p.TurnId))
                    {
                        p.End = CodexTime(payload, "completed_at");
                        if (p.End == DateTime.MinValue) p.End = ts;
                        if (p.End < p.Start) p.End = p.Start;
                        p.Anchor = DateTime.MinValue;
                        p.Interrupted = type == "turn_aborted";
                        p.Completed.Add(CodexTurn(p, p.End, false));
                        if (p.Completed.Count > 32) p.Completed.RemoveAt(0);
                    }
                }
                else if (type == "user_message" && string.IsNullOrEmpty(p.Title))
                {
                    SetCodexTitle(p, Str(payload, "message"));
                }
                else if (type == "token_count")
                {
                    Dictionary<string, object> usage = Map(Map(payload, "info"), "total_token_usage");
                    object value;
                    long total;
                    if (usage == null || !usage.TryGetValue("output_tokens", out value)
                        || !long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out total) || total < 0) return;
                    if (total == p.Total) return; // Repeated quota-only snapshots must not count twice.
                    Dictionary<string, object> last = Map(Map(payload, "info"), "last_token_usage");
                    long tokens = 0;
                    if (last != null && last.TryGetValue("output_tokens", out value))
                        long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out tokens);
                    if (p.Total >= 0 && total > p.Total) tokens = total - p.Total;
                    DateTime end = p.ResponseEnd != DateTime.MinValue ? p.ResponseEnd : ts;
                    double secs = (end - p.Anchor).TotalSeconds;
                    // token_count can be written after tool output: use the preceding response end.
                    if (p.Total <= total && tokens > 0 && p.Anchor != DateTime.MinValue && secs >= 0.5 && secs <= 900)
                        p.Reqs.Add(new Req { Start = p.Anchor, End = end, Tokens = tokens, Done = true });
                    p.Total = total;
                    p.Anchor = p.NextAnchor > end ? p.NextAnchor : ts;
                    p.ResponseEnd = p.NextAnchor = DateTime.MinValue;
                }
            }
            else if (kind == "response_item")
            {
                if (type == "message" && Str(payload, "role") == "user")
                {
                    Dictionary<string, object> meta = Map(payload, "internal_chat_message_metadata_passthrough");
                    string turn = Str(meta, "turn_id");
                    if (turn != null && p.TurnId != null && turn != p.TurnId) return;
                    object value;
                    if (meta != null && meta.TryGetValue("content_item_kinds", out value))
                    {
                        object[] kinds = value as object[];
                        if (kinds == null || Array.IndexOf(kinds, "user.text") < 0) return;
                    }
                    if (payload.TryGetValue("content", out value))
                    {
                        object[] content = value as object[];
                        if (content != null)
                        {
                            StringBuilder text = new StringBuilder();
                            foreach (object item in content)
                            {
                                Dictionary<string, object> block = item as Dictionary<string, object>;
                                if (Str(block, "type") == "input_text") text.AppendLine(Str(block, "text"));
                            }
                            SetCodexTitle(p, text.ToString());
                        }
                    }
                }
                else if (type == "function_call_output" || type == "custom_tool_call_output")
                    p.NextAnchor = ts;
                else if (type == "function_call" || type == "custom_tool_call" || (type == "message" && Str(payload, "role") == "assistant"))
                {
                    if (p.ResponseEnd == DateTime.MinValue && p.NextAnchor > p.Anchor)
                    {
                        p.Anchor = p.NextAnchor;
                        p.NextAnchor = DateTime.MinValue;
                    }
                    p.ResponseEnd = ts;
                }
            }
        }

        static void SetCodexTitle(CodexParsed p, string text)
        {
            if (p.Start == DateTime.MinValue || p.End != DateTime.MinValue || !string.IsNullOrEmpty(p.Title) || string.IsNullOrWhiteSpace(text)) return;
            text = text.Trim();
            if (text.StartsWith("# AGENTS.md instructions", StringComparison.Ordinal)
                || text.StartsWith("<environment_context>", StringComparison.Ordinal)
                || text.StartsWith("<external_codex_apps_open_page>", StringComparison.Ordinal)
                || text.StartsWith("<send_user_message_question_reply>", StringComparison.Ordinal)
                || text.StartsWith("<user_instructions>", StringComparison.Ordinal)) return;
            text = Regex.Replace(text, "\\s+", " ");
            p.Title = text.Length > 80 ? text.Substring(0, 80) + "…" : text;
        }

        static CodexParsed ReadCodexCached(FileInfo fi)
        {
            CodexParsed p;
            if (!codexCache.TryGetValue(fi.FullName, out p) || fi.Length < p.Offset
                || (fi.Length == p.Offset && fi.LastWriteTimeUtc != p.Mtime))
                codexCache[fi.FullName] = p = new CodexParsed();
            p.Path = fi.FullName;
            if (p.Offset == fi.Length && p.Mtime == fi.LastWriteTimeUtc) return p;
            using (FileStream fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (MemoryStream line = new MemoryStream())
            {
                fs.Position = p.Offset;
                long position = p.Offset, limit = fs.Length;
                byte[] buffer = new byte[65536];
                while (position < limit)
                {
                    int count = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - position));
                    if (count == 0) break;
                    int start = 0;
                    for (int i = 0; i < count; i++)
                    {
                        if (buffer[i] != 10) continue;
                        line.Write(buffer, start, i - start);
                        try { CodexLine(p, Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimStart('\uFEFF')); }
                        catch (ArgumentException) { } // A malformed complete line must not hide later events.
                        catch (InvalidOperationException) { }
                        line.SetLength(0);
                        start = i + 1;
                        p.Offset = position + start;
                    }
                    line.Write(buffer, start, count - start);
                    position += count;
                }
                // Leave an incomplete final UTF-8/JSON line unread until the writer finishes it.
            }
            p.Mtime = fi.LastWriteTimeUtc;
            if (p.Reqs.Count > 256) p.Reqs.RemoveRange(0, p.Reqs.Count - 256);
            return p;
        }

        static SessionTurn CodexTurn(CodexParsed p, DateTime end, bool abandoned)
        {
            return new SessionTurn { AI = "Codex", SessionId = p.SessionId, Path = p.Path, Title = p.Title, Project = p.Project,
                Start = p.Start.ToLocalTime(), End = end == DateTime.MinValue ? end : end.ToLocalTime(),
                Interrupted = p.Interrupted, Abandoned = abandoned };
        }

        static Dictionary<string, string> ReadCodexSessionNames(string sessionsDir)
        {
            Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                string home = Path.GetDirectoryName(sessionsDir.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(home)) return names;
                string path = Path.Combine(home, "session_index.jsonl");
                if (!File.Exists(path)) return names;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader reader = new StreamReader(fs, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        try
                        {
                            Dictionary<string, object> entry = json.DeserializeObject(line) as Dictionary<string, object>;
                            string id = Str(entry, "id"), name = Str(entry, "thread_name");
                            if (!string.IsNullOrEmpty(id) && !string.IsNullOrWhiteSpace(name)) names[id] = name.Trim();
                        }
                        catch (ArgumentException) { }
                        catch (InvalidOperationException) { }
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return names;
        }

        public static Tps ReadCodexTps(string sessionsDir)
        {
            Tps t = new Tps();
            try
            {
                List<string> files = RecentFiles(sessionsDir, 14);
                files.Sort(delegate(string a, string b) { return File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)); });
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Req last = null;
                long sumTokens = 0;
                double sumSeconds = 0;
                DateTime now = DateTime.UtcNow;
                bool activeSelected = false;
                int mainCount = 0;
                foreach (string file in files)
                {
                    FileInfo fi = new FileInfo(file);
                    if (mainCount >= 8 || fi.LastWriteTimeUtc < now.AddDays(-1)) break;
                    try
                    {
                        CodexParsed p = ReadCodexCached(fi);
                        seen.Add(file);
                        if (p.Subagent) continue;
                        mainCount++;
                        t.Sessions.AddRange(p.Completed);
                        DateTime end = p.End;
                        if (end == DateTime.MinValue && now - fi.LastWriteTimeUtc > TimeSpan.FromMinutes(30)) end = fi.LastWriteTimeUtc;
                        bool active = p.Start != DateTime.MinValue && end == DateTime.MinValue;
                        if (p.Start != DateTime.MinValue && p.End == DateTime.MinValue)
                            t.Sessions.Add(CodexTurn(p, end, end != DateTime.MinValue));
                        if (p.Start != DateTime.MinValue && ((!activeSelected && active) || (activeSelected == active && p.Start.ToLocalTime() > t.TurnStart)))
                        {
                            t.TurnStart = p.Start.ToLocalTime();
                            t.TurnEnd = end == DateTime.MinValue ? end : end.ToLocalTime();
                            activeSelected = active;
                        }
                        foreach (Req r in p.Reqs)
                        {
                            if (last == null || r.End > last.End) last = r;
                            if (r.End < now.AddMinutes(-Tps.AvgMinutes)) continue;
                            sumTokens += r.Tokens;
                            sumSeconds += (r.End - r.Start).TotalSeconds;
                            t.AvgCount++;
                        }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                List<string> stale = new List<string>();
                foreach (string key in codexCache.Keys) if (!seen.Contains(key)) stale.Add(key);
                foreach (string key in stale) codexCache.Remove(key);
                if (last != null)
                {
                    t.LastTokens = last.Tokens;
                    t.LastSecs = (last.End - last.Start).TotalSeconds;
                    t.Last = t.LastTokens / t.LastSecs;
                    t.LastAt = last.End.ToLocalTime();
                }
                if (sumSeconds > 0) t.Avg = sumTokens / sumSeconds;
                Dictionary<string, string> sessionNames = ReadCodexSessionNames(sessionsDir);
                foreach (SessionTurn session in t.Sessions)
                {
                    string name;
                    session.SessionTitle = session.SessionId != null && sessionNames.TryGetValue(session.SessionId, out name) ? name : null;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return t;
        }


        class Req
        {
            public string Id;
            public DateTime Start, End;
            public long Tokens;
            public bool Done;
        }

        static readonly Regex reTs = new Regex("\"uuid\":\"[^\"]+\",\"timestamp\":\"([^\"]+)\"", RegexOptions.Compiled);
        static readonly Regex reRole = new Regex("\"role\":\"(\\w+)\"", RegexOptions.Compiled);
        static readonly Regex reMsgId = new Regex("\"id\":\"(msg_[^\"]+)\"", RegexOptions.Compiled);
        static readonly Regex reOut = new Regex("\"usage\":\\{[^{}]*?\"output_tokens\":(\\d+)", RegexOptions.Compiled);
        static readonly Regex reStop = new Regex("\"stop_reason\":(null|\"\\w+\")", RegexOptions.Compiled);

        static DateTime Ts(string line)
        {
            Match m = reTs.Match(line);
            DateTime t;
            if (m.Success && DateTime.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t))
                return t;
            return DateTime.MinValue;
        }

        class Parsed
        {
            public long Length;
            public DateTime Mtime;
            public List<Req> Reqs = new List<Req>();
            public DateTime TurnStart = DateTime.MinValue, TurnEnd = DateTime.MinValue; // UTC
            public bool Interrupted;
            public string Title, Project;
        }

        static readonly Regex reTitle = new Regex("\"customTitle\":\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);
        static readonly Regex reAgentName = new Regex("\"agentName\":\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);
        static readonly Regex reCwd = new Regex("\"cwd\":\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

        static string JsonStr(string s)
        {
            try { return Regex.Unescape(s); } catch { return s; }
        }

        // Only the transcript being written changes between polls; the rest come from here.
        static readonly Dictionary<string, Parsed> parseCache = new Dictionary<string, Parsed>(StringComparer.OrdinalIgnoreCase);

        // A real prompt typed by the person, as opposed to tool results, skill bodies and other injected entries.
        static bool IsHumanPrompt(string line, string head)
        {
            if (line.Contains("\"isMeta\":true") || line.Contains("\"isCompactSummary\":true")) return false;
            if (head.Contains("tool_result") || head.Contains("[Request interrupted") || head.Contains("<local-command")) return false;
            int o = line.IndexOf("\"origin\":{\"kind\":\"", StringComparison.Ordinal);
            return o < 0 || string.CompareOrdinal(line, o + 18, "human\"", 0, 6) == 0;
        }

        // One assistant message is written as one line per content block, each stamped when the
        // block finished, all carrying the final usage. A request spans from the preceding user
        // entry (prompt or tool result) to the last block, so time-to-first-token is included.
        static Parsed ParseTranscript(string path, bool trackTurns)
        {
            Parsed p = new Parsed();
            List<Req> outReqs = p.Reqs;
            string text = ReadTail(path, 4 * 1024 * 1024);
            string[] lines = text.Split('\n');
            DateTime anchor = DateTime.MinValue;
            Req cur = null;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (trackTurns)
                {
                    // Session name lines are small and re-appended after turns; keep the latest.
                    if (line.StartsWith("{\"type\":\"custom-title\"", StringComparison.Ordinal))
                    {
                        Match tm = reTitle.Match(line);
                        if (tm.Success) p.Title = JsonStr(tm.Groups[1].Value);
                        continue;
                    }
                    if (line.StartsWith("{\"type\":\"agent-name\"", StringComparison.Ordinal))
                    {
                        Match am = reAgentName.Match(line);
                        if (am.Success && string.IsNullOrEmpty(p.Title)) p.Title = JsonStr(am.Groups[1].Value);
                        continue;
                    }
                }
                int mi = line.IndexOf("\"message\":{", StringComparison.Ordinal);
                if (mi < 0) continue;
                string head = line.Substring(mi, Math.Min(400, line.Length - mi));
                Match role = reRole.Match(head);
                if (!role.Success) continue;
                DateTime ts = Ts(line);
                if (ts == DateTime.MinValue) continue;

                if (role.Groups[1].Value == "user")
                {
                    if (cur != null) { outReqs.Add(cur); cur = null; }
                    anchor = ts;
                    if (trackTurns)
                    {
                        if (IsHumanPrompt(line, head))
                        {
                            p.TurnStart = ts;
                            p.TurnEnd = DateTime.MinValue;
                            p.Interrupted = false;
                            Match cm = reCwd.Match(line);
                            if (cm.Success) p.Project = Path.GetFileName(JsonStr(cm.Groups[1].Value).TrimEnd('\\', '/'));
                        }
                        else if (head.Contains("[Request interrupted") && p.TurnEnd == DateTime.MinValue)
                        {
                            p.TurnEnd = ts;
                            p.Interrupted = true;
                        }
                    }
                }
                else if (role.Groups[1].Value == "assistant")
                {
                    if (head.Contains("\"model\":\"<synthetic>\"")) continue;
                    Match id = reMsgId.Match(head);
                    if (!id.Success) continue;
                    if (cur == null || cur.Id != id.Groups[1].Value)
                    {
                        if (cur != null) { outReqs.Add(cur); anchor = cur.End; }
                        cur = new Req();
                        cur.Id = id.Groups[1].Value;
                        cur.Start = anchor;
                    }
                    cur.End = ts;
                    Match o = reOut.Match(line);
                    if (o.Success) long.TryParse(o.Groups[1].Value, out cur.Tokens);
                    Match s = reStop.Match(line);
                    string stop = s.Success ? s.Groups[1].Value : "null";
                    cur.Done = stop != "null";
                    // Any stop other than a tool call (or server pause) ends the turn.
                    if (trackTurns && cur.Done && stop != "\"tool_use\"" && stop != "\"pause_turn\"" && p.TurnStart != DateTime.MinValue)
                        p.TurnEnd = ts;
                }
            }
            if (cur != null) outReqs.Add(cur);
            return p;
        }

        static Parsed ParseCached(FileInfo fi)
        {
            Parsed p;
            if (parseCache.TryGetValue(fi.FullName, out p) && p.Length == fi.Length && p.Mtime == fi.LastWriteTimeUtc) return p;
            bool isSubagent = fi.FullName.IndexOf(Path.DirectorySeparatorChar + "subagents" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0;
            p = ParseTranscript(fi.FullName, !isSubagent);
            p.Length = fi.Length;
            p.Mtime = fi.LastWriteTimeUtc;
            parseCache[fi.FullName] = p;
            return p;
        }

        // Main sessions and subagents of the CLI and the desktop app all log under <config>/projects.
        public static Tps ReadClaudeTps(string projectsDir)
        {
            Tps t = new Tps();
            try
            {
                if (!Directory.Exists(projectsDir)) return t;
                DateTime cutoff = DateTime.UtcNow.AddDays(-1);
                List<FileInfo> files = new List<FileInfo>();
                foreach (string f in Directory.EnumerateFiles(projectsDir, "*.jsonl", SearchOption.AllDirectories))
                {
                    FileInfo fi = new FileInfo(f);
                    if (fi.LastWriteTimeUtc >= cutoff) files.Add(fi);
                }
                files.Sort(delegate(FileInfo a, FileInfo b) { return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc); });

                List<Req> reqs = new List<Req>();
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Parsed turn = null;
                FileInfo turnFile = null;
                for (int i = 0; i < files.Count && i < 6; i++)
                {
                    try
                    {
                        Parsed p = ParseCached(files[i]);
                        seen.Add(files[i].FullName);
                        reqs.AddRange(p.Reqs);
                        if (p.TurnStart != DateTime.MinValue && (turn == null || p.TurnStart > turn.TurnStart)) { turn = p; turnFile = files[i]; }
                        if (p.TurnStart != DateTime.MinValue)
                        {
                            SessionTurn st = new SessionTurn();
                            st.Path = files[i].FullName;
                            st.Title = p.Title;
                            st.Project = p.Project;
                            st.Start = p.TurnStart.ToLocalTime();
                            st.Interrupted = p.Interrupted;
                            if (p.TurnEnd != DateTime.MinValue) st.End = p.TurnEnd.ToLocalTime();
                            else if (DateTime.UtcNow - files[i].LastWriteTimeUtc > TimeSpan.FromMinutes(30))
                            {
                                st.End = files[i].LastWriteTime;
                                st.Abandoned = true;
                            }
                            t.Sessions.Add(st);
                        }
                    }
                    catch { }
                }
                List<string> stale = new List<string>();
                foreach (string k in parseCache.Keys) if (!seen.Contains(k)) stale.Add(k);
                foreach (string k in stale) parseCache.Remove(k);

                if (turn != null)
                {
                    t.TurnStart = turn.TurnStart.ToLocalTime();
                    DateTime end = turn.TurnEnd;
                    // A session closed mid-turn never logs an end; stop the clock at its last write.
                    if (end == DateTime.MinValue && DateTime.UtcNow - turnFile.LastWriteTimeUtc > TimeSpan.FromMinutes(30))
                        end = turnFile.LastWriteTimeUtc;
                    if (end != DateTime.MinValue) t.TurnEnd = end.ToLocalTime();
                }

                DateTime avgFrom = DateTime.UtcNow.AddMinutes(-Tps.AvgMinutes);
                Req last = null;
                long sumTok = 0;
                double sumSecs = 0;
                foreach (Req r in reqs)
                {
                    if (!r.Done || r.Tokens <= 0 || r.Start == DateTime.MinValue) continue;
                    double secs = (r.End - r.Start).TotalSeconds;
                    // Skip blocks with no real streaming time and gaps that span idle/resumed sessions.
                    if (secs < 0.5 || secs > 900) continue;
                    if (last == null || r.End > last.End) last = r;
                    if (r.End >= avgFrom) { sumTok += r.Tokens; sumSecs += secs; t.AvgCount++; }
                }
                if (last != null)
                {
                    t.LastTokens = last.Tokens;
                    t.LastSecs = (last.End - last.Start).TotalSeconds;
                    t.Last = last.Tokens / t.LastSecs;
                    t.LastAt = last.End.ToLocalTime();
                }
                if (sumSecs > 0) t.Avg = sumTok / sumSecs;
            }
            catch { }
            return t;
        }
    }

    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int CX, CY; public SIZE(int x, int y) { CX = x; CY = y; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    }

    class Item
    {
        public string Name;
        public string LongName;
        public Usage Data;
        public Color Accent;
        public Item(string name, string longName, Usage data, Color accent) { Name = name; LongName = longName; Data = data; Accent = accent; }
    }

    public class WidgetForm : Form
    {
        readonly string claudePath, codexDir, configPath, projectsDir, credentialsPath;
        Usage claude = new Usage(), codex = new Usage();
        Usage claudeApi;  // last successful OAuth usage fetch
        int apiBusy = 0;
        Tps claudeTps = new Tps(), codexTps = new Tps();
        const int ModeShort = 0, ModeWeek = 1, ModeBoth = 2;
        int limitMode = ModeBoth;
        bool vertical = false; // one row per AI instead of a single row
        ToolStripMenuItem miHorizontal, miVertical;
        bool showUsed = false, hover = false;
        // Appearance: text color follows the taskbar theme unless set; background is off at 0% opacity.
        Color? customFg = null;
        Color? customBg = null;
        int bgOpacity = 0;
        static readonly int[] OpacitySteps = { 0, 20, 40, 60, 80, 100 };
        ToolStripMenuItem miFgAuto, miBgAuto, miOpacity;
        bool showClaude = true, showCodex = true, showTps = true;
        // Turn-finished notifications: one per (session, turn), only for turns ending after launch.
        bool notifyOn = true, notifySound = false, notifyBlink = true;
        readonly DateTime launchedAt = DateTime.Now;
        readonly HashSet<string> notifiedTurn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly List<ToastForm> toasts = new List<ToastForm>();
        ToolStripMenuItem miNotify, miNotifySound, miNotifyBlink, miToastSize;
        float toastZoom = 1f; // card size, independent of the widget's own zoom
        static readonly int[] ToastSteps = { 80, 100, 125, 150, 200 };
        float scale = 1f;
        bool hasSavedPos = false;
        bool placed = false; // set once PlaceInitial has positioned the window
        int savedRight = int.MinValue; // right edge, so a wider widget grows left instead of into the tray
        Point savedPos;
        ToolTip tip;
        string lastTip = "";
        ContextMenuStrip menu;
        ToolStripMenuItem miShort, miWeek, miBoth, miUsed, miShowClaude, miShowCodex, miShowTps;
        System.Windows.Forms.Timer dataTimer, topTimer, apiTimer, tickTimer;

        public WidgetForm(string dataDir)
        {
            claudePath = Path.Combine(dataDir, "claude-usage.json");
            configPath = Path.Combine(dataDir, "config.ini");
            string codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrEmpty(codexHome))
                codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            codexDir = Path.Combine(codexHome, "sessions");
            string claudeHome = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrEmpty(claudeHome))
                claudeHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            projectsDir = Path.Combine(claudeHome, "projects");
            credentialsPath = Path.Combine(claudeHome, ".credentials.json");

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Size = new Size(1, 1);
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;

            LoadConfig();

            tip = new ToolTip();
            tip.ShowAlways = true;
            tip.InitialDelay = 300;
            tip.AutoPopDelay = 30000;

            BuildMenu();

            dataTimer = new System.Windows.Forms.Timer();
            dataTimer.Interval = 5000;
            dataTimer.Tick += delegate { RefreshData(); };
            topTimer = new System.Windows.Forms.Timer();
            topTimer.Interval = 1500;
            topTimer.Tick += delegate { KeepOnTop(); };
            apiTimer = new System.Windows.Forms.Timer();
            apiTimer.Interval = 120000;
            apiTimer.Tick += delegate { FetchClaudeApi(); };
            // Redraw every second while a turn is running so the elapsed time ticks.
            tickTimer = new System.Windows.Forms.Timer();
            tickTimer.Interval = 1000;
            tickTimer.Tick += delegate { if (showTps && ((showClaude && claudeTps.TurnActive) || (showCodex && codexTps.TurnActive)) && Visible) Render(); };

            SystemEvents.DisplaySettingsChanged += delegate { ClampToScreen(); Render(); };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // layered | toolwindow (no alt-tab) | noactivate | topmost
                cp.ExStyle |= 0x80000 | 0x80 | 0x08000000 | 0x8;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            RefreshData();
            PlaceInitial();
            dataTimer.Start();
            topTimer.Start();
            FetchClaudeApi();
            apiTimer.Start();
            tickTimer.Start();
            if (PreviewOnStart) ShowDemoToast();
        }

        // Network call runs off the UI thread; at most one in flight.
        void FetchClaudeApi()
        {
            if (Interlocked.CompareExchange(ref apiBusy, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Usage u = Sources.ReadClaudeApi(credentialsPath);
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (u != null) claudeApi = u;
                        apiBusy = 0;
                        RefreshData();
                    });
                }
                catch { apiBusy = 0; }
            });
        }

        int S(float px) { return (int)Math.Round(px * scale); }
        // Content size: DPI scale times the user's zoom (dragging the left/right edge).
        int Z(float px) { return (int)Math.Round(px * scale * zoom); }

        const float ZoomMin = 0.6f, ZoomMax = 2.5f;
        float zoom = 1f;
        // Edge being dragged: horizontal -1 left / 1 right, vertical -1 top / 1 bottom, 0 none.
        int resizeH = 0, resizeV = 0;
        bool Resizing { get { return resizeH != 0 || resizeV != 0; } }
        int rsStartX, rsStartY, rsStartW, rsStartH;
        float rsStartZoom;
        static readonly int[] ZoomSteps = { 80, 100, 125, 150, 200 };
        ToolStripMenuItem miZoom;

        // Corners get a larger hit zone so diagonal drags are easy to grab.
        void HitEdge(Point p, out int h, out int v)
        {
            int z = Math.Max(4, S(6)), c = Math.Max(8, S(12));
            h = 0;
            v = 0;
            bool nearL = p.X <= c, nearR = p.X >= Width - c, nearT = p.Y <= c, nearB = p.Y >= Height - c;
            if ((nearL || nearR) && (nearT || nearB))
            {
                h = nearL ? -1 : 1;
                v = nearT ? -1 : 1;
                return;
            }
            if (p.X <= z) h = -1;
            else if (p.X >= Width - z) h = 1;
            if (p.Y <= z) v = -1;
            else if (p.Y >= Height - z) v = 1;
        }

        static Cursor EdgeCursor(int h, int v)
        {
            if (h != 0 && v != 0) return h == v ? Cursors.SizeNWSE : Cursors.SizeNESW;
            if (h != 0) return Cursors.SizeWE;
            if (v != 0) return Cursors.SizeNS;
            return Cursors.Default;
        }

        void SetZoom(float z)
        {
            z = (float)Math.Round(Math.Max(ZoomMin, Math.Min(ZoomMax, z)), 2);
            if (Math.Abs(z - zoom) < 0.005f) return;
            zoom = z;
            Render();
        }

        int BarHeight()
        {
            Screen s = Screen.FromPoint(new Point(Left + Width / 2, Top + Height / 2));
            int h = s.Bounds.Bottom - s.WorkingArea.Bottom;
            return h >= S(24) ? h : S(48);
        }

        void PlaceInitial()
        {
            placed = false;
            Render();
            int x, y;
            if (hasSavedPos)
            {
                x = savedRight != int.MinValue ? savedRight - Width : savedPos.X;
                y = savedPos.Y;
            }
            else
            {
                Screen s = Screen.PrimaryScreen;
                // Left of the tray and the Windows 11 weather widget; drag to move.
                x = s.WorkingArea.Right - Width - S(440);
                y = s.Bounds.Bottom - Height;
            }
            Location = new Point(x, y);
            ClampToScreen();
            placed = true;
            Render();
        }

        void ClampToScreen()
        {
            Rectangle vs = SystemInformation.VirtualScreen;
            int x = Math.Max(vs.Left, Math.Min(Left, vs.Right - Width));
            int y = Math.Max(vs.Top, Math.Min(Top, vs.Bottom - Height));
            if (x != Left || y != Top) Location = new Point(x, y);
        }

        void BuildMenu()
        {
            menu = new ContextMenuStrip();
            ToolStripMenuItem miShow = new ToolStripMenuItem("표시할 AI");
            miShowClaude = new ToolStripMenuItem("Claude Code", null, delegate { ToggleShow(ref showClaude, showCodex); });
            miShowCodex = new ToolStripMenuItem("Codex", null, delegate { ToggleShow(ref showCodex, showClaude); });
            miShow.DropDownItems.Add(miShowClaude);
            miShow.DropDownItems.Add(miShowCodex);
            miShow.DropDownItems.Add(new ToolStripSeparator());
            miShowTps = new ToolStripMenuItem("출력 속도·작업시간 (Claude / Codex)", null, delegate { showTps = !showTps; SaveConfig(); UpdateMenu(); RefreshData(); });
            miShow.DropDownItems.Add(miShowTps);
            ToolStripMenuItem miNotifyRoot = new ToolStripMenuItem("작업 완료 알림");
            miNotify = new ToolStripMenuItem("알림 켜기", null, delegate { notifyOn = !notifyOn; SaveConfig(); UpdateMenu(); RefreshData(); });
            miNotifySound = new ToolStripMenuItem("알림 소리", null, delegate { notifySound = !notifySound; SaveConfig(); UpdateMenu(); });
            miNotifyRoot.DropDownItems.Add(miNotify);
            miNotifyRoot.DropDownItems.Add(miNotifySound);
            miNotifyBlink = new ToolStripMenuItem("깜박임", null, delegate { notifyBlink = !notifyBlink; SaveConfig(); UpdateMenu(); });
            miNotifyRoot.DropDownItems.Add(miNotifyBlink);
            miToastSize = new ToolStripMenuItem("알림 크기");
            foreach (int step in ToastSteps)
            {
                int tv = step;
                ToolStripMenuItem mi = new ToolStripMenuItem(tv + "%", null, delegate
                {
                    toastZoom = tv / 100f;
                    SaveConfig();
                    UpdateMenu();
                    ShowDemoToast(); // show the new size right away
                });
                mi.Tag = tv;
                miToastSize.DropDownItems.Add(mi);
            }
            miNotifyRoot.DropDownItems.Add(miToastSize);
            miNotifyRoot.DropDownItems.Add("알림 미리보기", null, delegate { ShowDemoToast(); });
            menu.Items.Add(miShow);
            menu.Items.Add(miNotifyRoot);
            menu.Items.Add(new ToolStripSeparator());
            miShort =new ToolStripMenuItem("5시간 한도 표시", null, delegate { SetMode(ModeShort); });
            miWeek = new ToolStripMenuItem("주간 한도 표시", null, delegate { SetMode(ModeWeek); });
            miBoth = new ToolStripMenuItem("5시간 + 주간 함께 표시", null, delegate { SetMode(ModeBoth); });
            miUsed = new ToolStripMenuItem("사용한 양으로 표시", null, delegate { showUsed = !showUsed; SaveConfig(); UpdateMenu(); Render(); });
            menu.Items.Add(miShort);
            menu.Items.Add(miWeek);
            menu.Items.Add(miBoth);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miUsed);
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem miLook = new ToolStripMenuItem("모양");
            miLook.DropDownItems.Add("글자 색상 선택...", null, delegate { PickColor(true); });
            miFgAuto = new ToolStripMenuItem("글자 색상 자동 (작업표시줄 테마)", null, delegate { customFg = null; ApplyLook(); });
            miLook.DropDownItems.Add(miFgAuto);
            miLook.DropDownItems.Add(new ToolStripSeparator());
            miLook.DropDownItems.Add("배경 색상 선택...", null, delegate { PickColor(false); });
            miBgAuto = new ToolStripMenuItem("배경 색상 자동 (작업표시줄 테마)", null, delegate { customBg = null; ApplyLook(); });
            miLook.DropDownItems.Add(miBgAuto);
            miOpacity = new ToolStripMenuItem("배경 불투명도");
            foreach (int step in OpacitySteps)
            {
                int pv = step;
                ToolStripMenuItem mi = new ToolStripMenuItem(pv == 0 ? "0% (투명)" : pv + "%", null, delegate { bgOpacity = pv; ApplyLook(); });
                mi.Tag = pv;
                miOpacity.DropDownItems.Add(mi);
            }
            miLook.DropDownItems.Add(miOpacity);
            miLook.DropDownItems.Add(new ToolStripSeparator());
            miHorizontal = new ToolStripMenuItem("가로 배치", null, delegate { vertical = false; ApplyLook(); });
            miVertical = new ToolStripMenuItem("세로 배치 (AI별 한 줄)", null, delegate { vertical = true; ApplyLook(); });
            miLook.DropDownItems.Add(miHorizontal);
            miLook.DropDownItems.Add(miVertical);
            miLook.DropDownItems.Add(new ToolStripSeparator());
            miZoom = new ToolStripMenuItem("크기 (가장자리·모서리 드래그로도 조절)");
            foreach (int step in ZoomSteps)
            {
                int zv = step;
                ToolStripMenuItem mi = new ToolStripMenuItem(zv + "%", null, delegate { SetZoom(zv / 100f); ApplyLook(); });
                mi.Tag = zv;
                miZoom.DropDownItems.Add(mi);
            }
            miLook.DropDownItems.Add(miZoom);
            miLook.DropDownItems.Add(new ToolStripSeparator());
            miLook.DropDownItems.Add("모양 기본값으로", null, delegate { customFg = null; customBg = null; bgOpacity = 0; SetZoom(1f); ApplyLook(); });
            menu.Items.Add(miLook);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("지금 새로고침", null, delegate { RefreshData(); FetchClaudeApi(); });
            menu.Items.Add("위치 초기화", null, delegate { hasSavedPos = false; PlaceInitial(); SaveConfig(); });
            menu.Items.Add(new ToolStripSeparator());
            miStartup = new ToolStripMenuItem("Windows 시작 시 자동 실행", null, delegate { SetStartup(!StartupEnabled()); });
            menu.Items.Add(miStartup);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("종료", null, delegate { Close(); });
            // Startup state can change outside the widget (Task Manager, moved exe), so re-read on open.
            menu.Opening += delegate { UpdateMenu(); };
            UpdateMenu();
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        const string RunName = "UsageWidget";
        ToolStripMenuItem miStartup;

        // Registered for this exe's current path and not switched off in Task Manager's Startup apps.
        static bool StartupEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    string v = k == null ? null : k.GetValue(RunName) as string;
                    if (v == null || !string.Equals(v.Trim().Trim('"'), Application.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                using (RegistryKey a = Registry.CurrentUser.OpenSubKey(ApprovedKey))
                {
                    byte[] b = a == null ? null : a.GetValue(RunName) as byte[];
                    if (b != null && b.Length > 0 && (b[0] & 1) == 1) return false; // odd first byte = disabled
                }
                return true;
            }
            catch { return false; }
        }

        void SetStartup(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunName, false);
                }
                // Clear any Task Manager "disabled" mark so turning it on really takes effect.
                using (RegistryKey a = Registry.CurrentUser.OpenSubKey(ApprovedKey, true))
                    if (a != null) a.DeleteValue(RunName, false);
            }
            catch (Exception ex)
            {
                MessageBox.Show("시작 프로그램 설정을 바꾸지 못했습니다.\n" + ex.Message, "UsageWidget");
            }
            UpdateMenu();
        }

        static Color? ParseHex(string v)
        {
            v = v.Trim().TrimStart('#');
            int rgb;
            if (v.Length != 6 || !int.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb)) return null;
            return Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
        }

        static string ToHex(Color? c)
        {
            return c.HasValue ? "#" + c.Value.R.ToString("X2") + c.Value.G.ToString("X2") + c.Value.B.ToString("X2") : "auto";
        }

        void ApplyLook()
        {
            SaveConfig();
            UpdateMenu();
            Render();
        }

        void PickColor(bool text)
        {
            using (ColorDialog cd = new ColorDialog())
            {
                cd.FullOpen = true;
                bool light = IsLightTaskbar();
                cd.Color = text ? (customFg ?? AutoFg(light)) : (customBg ?? AutoBg(light));
                if (cd.ShowDialog(this) != DialogResult.OK) return;
                if (text) customFg = cd.Color;
                else
                {
                    customBg = cd.Color;
                    // A color picked while fully transparent would be invisible.
                    if (bgOpacity == 0) bgOpacity = 60;
                }
                ApplyLook();
            }
        }

        static Color AutoFg(bool light) { return light ? Color.FromArgb(28, 28, 28) : Color.FromArgb(242, 242, 242); }
        static Color AutoBg(bool light) { return light ? Color.FromArgb(243, 243, 243) : Color.FromArgb(32, 32, 32); }

        void SetMode(int mode)
        {
            limitMode = mode;
            SaveConfig();
            UpdateMenu();
            Render();
        }

        // At least one AI must stay visible.
        void ToggleShow(ref bool flag, bool other)
        {
            if (flag && !other) return;
            flag = !flag;
            SaveConfig();
            UpdateMenu();
            Render();
        }

        void UpdateMenu()
        {
            miShowClaude.Checked = showClaude;
            miShowCodex.Checked = showCodex;
            miShowTps.Checked = showTps;
            miNotify.Checked = notifyOn;
            miNotifySound.Checked = notifySound;
            miNotifyBlink.Checked = notifyBlink;
            if (miStartup != null) miStartup.Checked = StartupEnabled();
            foreach (ToolStripItem it in miToastSize.DropDownItems)
            {
                ToolStripMenuItem mi = it as ToolStripMenuItem;
                if (mi != null) mi.Checked = Math.Abs((int)mi.Tag / 100f - toastZoom) < 0.005f;
            }
            miShort.Checked = limitMode == ModeShort;
            miWeek.Checked = limitMode == ModeWeek;
            miBoth.Checked = limitMode == ModeBoth;
            miUsed.Checked = showUsed;
            miFgAuto.Checked = !customFg.HasValue;
            miBgAuto.Checked = !customBg.HasValue;
            foreach (ToolStripItem it in miOpacity.DropDownItems)
            {
                ToolStripMenuItem mi = it as ToolStripMenuItem;
                if (mi != null) mi.Checked = (int)mi.Tag == bgOpacity;
            }
            foreach (ToolStripItem it in miZoom.DropDownItems)
            {
                ToolStripMenuItem mi = it as ToolStripMenuItem;
                if (mi != null) mi.Checked = Math.Abs((int)mi.Tag / 100f - zoom) < 0.005f;
            }
            miHorizontal.Checked = !vertical;
            miVertical.Checked = vertical;
            miZoom.Text = "크기 " + Math.Round(zoom * 100) + "% (가장자리·모서리 드래그로도 조절)";
        }

        void LoadConfig()
        {
            try
            {
                if (!File.Exists(configPath)) return;
                int x = int.MinValue, y = int.MinValue;
                foreach (string line in File.ReadAllLines(configPath))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    if (k == "x") int.TryParse(v, out x);
                    else if (k == "y") int.TryParse(v, out y);
                    else if (k == "r") { int rv; if (int.TryParse(v, out rv)) savedRight = rv; }
                    else if (k == "weekly") { int mode; if (int.TryParse(v, out mode) && mode >= ModeShort && mode <= ModeBoth) limitMode = mode; }
                    else if (k == "used") showUsed = v == "1";
                    else if (k == "claude") showClaude = v != "0";
                    else if (k == "codex") showCodex = v != "0";
                    else if (k == "tps") showTps = v != "0";
                    else if (k == "notify") notifyOn = v != "0";
                    else if (k == "sound") notifySound = v == "1";
                    else if (k == "blink") notifyBlink = v != "0";
                    else if (k == "toastzoom") { float tz; if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out tz)) toastZoom = Math.Max(0.5f, Math.Min(3f, tz)); }
                    else if (k == "layout") vertical = v == "vertical";
                    else if (k == "zoom") { float zv; if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out zv)) zoom = Math.Max(ZoomMin, Math.Min(ZoomMax, zv)); }
                    else if (k == "fg") customFg = ParseHex(v);
                    else if (k == "bg") customBg = ParseHex(v);
                    else if (k == "bgop") { int o; if (int.TryParse(v, out o)) bgOpacity = Math.Max(0, Math.Min(100, o)); }
                }
                if (x != int.MinValue && y != int.MinValue) { savedPos = new Point(x, y); hasSavedPos = true; }
            }
            catch { }
        }

        void SaveConfig()
        {
            try
            {
                File.WriteAllText(configPath,
                    "x=" + Left + "\r\ny=" + Top + "\r\nr=" + (Left + Width) +"\r\nweekly=" + limitMode + "\r\nused=" + (showUsed ? "1" : "0")
                    + "\r\nclaude=" + (showClaude ? "1" : "0") + "\r\ncodex=" + (showCodex ? "1" : "0") + "\r\ntps=" + (showTps ? "1" : "0")
                    + "\r\nnotify=" + (notifyOn ? "1" : "0") + "\r\nsound=" + (notifySound ? "1" : "0") + "\r\nblink=" + (notifyBlink ? "1" : "0")
                    + "\r\ntoastzoom=" + toastZoom.ToString("0.00", CultureInfo.InvariantCulture)
                    + "\r\nfg=" + ToHex(customFg) + "\r\nbg=" + ToHex(customBg) + "\r\nbgop=" + bgOpacity
                    + "\r\nzoom=" + zoom.ToString("0.00", CultureInfo.InvariantCulture)
                    + "\r\nlayout=" + (vertical ? "vertical" : "horizontal") + "\r\n");
            }
            catch { }
        }

        public static bool PreviewOnStart; // "-preview": show a sample card once at launch

        void ShowDemoToast()
        {
            SessionTurn demo = new SessionTurn();
            demo.Title = "예시 세션 제목";
            demo.Project = "usage-widget";
            demo.End = DateTime.Now;
            demo.Start = demo.End.AddSeconds(-84);
            ShowToast(demo);
        }

        void CheckFinishedTurns()
        {
            List<SessionTurn> sessions = new List<SessionTurn>(claudeTps.Sessions);
            sessions.AddRange(codexTps.Sessions);
            foreach (SessionTurn s in sessions)
            {
                if (!TryMarkNotification(s, launchedAt, DateTime.Now, notifiedTurn)) continue;
                ShowToast(s);
            }
        }

        public static bool TryMarkNotification(SessionTurn s, DateTime launched, DateTime now, HashSet<string> seen)
        {
            if (s.Start == DateTime.MinValue || s.End == DateTime.MinValue || s.End < s.Start
                || s.Interrupted || s.Abandoned || s.End <= launched || s.End > now
                || (now - s.End).TotalMinutes > 5) return false;
            return seen.Add(s.AI + "|" + s.Path + "|" + s.Start.Ticks.ToString(CultureInfo.InvariantCulture));
        }

        void ShowToast(SessionTurn s)
        {
            bool light = IsLightTaskbar();
            string label = !string.IsNullOrEmpty(s.SessionTitle) ? s.SessionTitle : s.Title;
            string title = !string.IsNullOrEmpty(label) ? label : (!string.IsNullOrEmpty(s.Project) ? s.Project : s.AI);
            string sub = (!string.IsNullOrEmpty(label) && !string.IsNullOrEmpty(s.Project) ? s.Project + " · " : "")
                + s.End.ToString("HH:mm") + " 완료";
            Color accent = s.AI == "Codex" ? Color.FromArgb(79, 142, 247) : Color.FromArgb(217, 119, 87);
            ToastForm t = new ToastForm("✓ " + s.AI + " 작업 완료 · " + FmtDur(s.End - s.Start, false), title, sub,
                customBg ?? AutoBg(light), customFg ?? AutoFg(light), accent, scale * toastZoom, 10, notifyBlink ? 5 : 0);
            t.FormClosed += delegate { toasts.Remove(t); LayoutToasts(); };
            toasts.Add(t);
            while (toasts.Count > 4) { ToastForm old = toasts[0]; toasts.RemoveAt(0); old.Close(); }
            LayoutToasts();
            t.Show();
            if (notifySound) System.Media.SystemSounds.Asterisk.Play();
        }

        // Stack cards above the widget (below it when the widget sits in the top half), newest nearest.
        void LayoutToasts()
        {
            Screen sc = Screen.FromRectangle(Bounds);
            bool above = Top + Height / 2 > sc.Bounds.Top + sc.Bounds.Height / 2;
            int gapPx = S(6);
            int y = above ? Top - gapPx : Bottom + gapPx;
            for (int i = toasts.Count - 1; i >= 0; i--)
            {
                ToastForm t = toasts[i];
                int x = Math.Max(sc.Bounds.Left, Math.Min(Right - t.Width, sc.Bounds.Right - t.Width));
                if (above) { y -= t.Height; t.Location = new Point(x, y); y -= gapPx; }
                else { t.Location = new Point(x, y); y += t.Height + gapPx; }
            }
        }

        void RefreshData()
        {
            // Prefer whichever is newer: the OAuth API poll or the CLI status line snapshot.
            Usage file = Sources.ReadClaude(claudePath);
            claude = (claudeApi != null && claudeApi.Updated >= file.Updated) ? claudeApi : file;
            codex = Sources.ReadCodex(codexDir);
            if (showTps || notifyOn) claudeTps = Sources.ReadClaudeTps(projectsDir);
            if ((showTps && showCodex) || notifyOn) codexTps = Sources.ReadCodexTps(codexDir);
            if (notifyOn) CheckFinishedTurns();
            Render();
        }

        static bool IsLightTaskbar()
        {
            try
            {
                object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0);
                return v is int && (int)v == 1;
            }
            catch { return false; }
        }

        Item[] Items()
        {
            List<Item> list = new List<Item>();
            if (showClaude) list.Add(new Item("Claude", "Claude Code", claude, Color.FromArgb(217, 119, 87)));
            if (showCodex) list.Add(new Item("Codex", "Codex", codex, Color.FromArgb(79, 142, 247)));
            return list.ToArray();
        }

        void Render()
        {
            if (!IsHandleCreated) return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            bool light = IsLightTaskbar();
            Color fg = customFg ?? AutoFg(light);
            Color dim = Color.FromArgb(light ? 165 : 175, fg);
            Item[] items = Items();

            int pad = Z(8), ringD = Z(20), penW = Math.Max(2, Z(3)), gap = Z(6), spacing = Z(12);

            using (Font fLabel = new Font("Segoe UI", 10.5f * scale * zoom, FontStyle.Regular, GraphicsUnit.Pixel))
            using (Font fValue = new Font("Segoe UI", 13f * scale * zoom, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                float[] textW = new float[items.Length];
                float lh, vh, tpsW = 0, timeW = 0, valW, secGap = Z(4);
                int boltW = Z(12), timeGap = Z(10);
                string tpsLabel = "t/s";
                bool both = limitMode == ModeBoth;
                using (Bitmap tmp = new Bitmap(1, 1))
                using (Graphics mg = Graphics.FromImage(tmp))
                {
                    mg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    valW = mg.MeasureString("100%", fValue).Width;
                    // In "both" mode the weekly value sits after the 5-hour value: "66%  주 65%".
                    float valueRow = both ? valW + secGap + mg.MeasureString("주 100%", fLabel).Width : valW;
                    for (int i = 0; i < items.Length; i++)
                        textW[i] = Math.Max(mg.MeasureString(items[i].Name, fLabel).Width, valueRow);
                    if (showTps)
                    {
                        tpsW = Math.Max(mg.MeasureString(tpsLabel, fLabel).Width, mg.MeasureString("88.8", fValue).Width);
                        timeW = Math.Max(mg.MeasureString("작업시간", fLabel).Width, mg.MeasureString("59m 59s", fValue).Width);
                    }
                    lh = fLabel.GetHeight(mg);
                    vh = fValue.GetHeight(mg);
                }
                // Shift for drawing small text on the same baseline as the bold value.
                float baseShift = fValue.Size * fValue.FontFamily.GetCellAscent(fValue.Style) / fValue.FontFamily.GetEmHeight(fValue.Style)
                    - fLabel.Size * fLabel.FontFamily.GetCellAscent(fLabel.Style) / fLabel.FontFamily.GetEmHeight(fLabel.Style);
                // Rows of segments: horizontal puts everything on one row, vertical gives each AI
                // its own row. A negative entry is the metrics segment of the AI before it.
                List<List<int>> rows = new List<List<int>>();
                for (int i = 0; i < items.Length; i++)
                {
                    if (vertical || rows.Count == 0) rows.Add(new List<int>());
                    rows[rows.Count - 1].Add(i);
                    if (showTps) rows[rows.Count - 1].Add(-i - 1);
                }
                // In vertical mode every AI column takes the widest text so the metrics line up.
                float widestText = 0;
                foreach (float w in textW) widestText = Math.Max(widestText, w);
                float metricsW = boltW + gap + tpsW + timeGap + timeW;

                float total = 0;
                foreach (List<int> row in rows)
                {
                    float rw = pad * 2 + spacing * (row.Count - 1);
                    foreach (int i in row) rw += i < 0 ? metricsW : ringD + gap + (vertical ? widestText : textW[i]);
                    total = Math.Max(total, rw);
                }
                int W = (int)Math.Ceiling(total);

                // One row: taskbar height, or taller when zoomed in far enough that the text needs it.
                float content = Math.Max(lh + vh - Z(3), ringD);
                int rowH = rows.Count > 1 ? (int)Math.Ceiling(content + Z(8)) : Math.Max(BarHeight(), (int)Math.Ceiling(content + Z(14)));
                int H = rows.Count > 1 ? rowH * rows.Count + Z(10) : rowH;
                int rowsTop = (H - rowH * rows.Count) / 2;

                using (Bitmap bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                        // Alpha 1 keeps the whole area clickable while looking transparent.
                        g.Clear(Color.FromArgb(1, 0, 0, 0));
                        if (bgOpacity > 0)
                            using (SolidBrush bgB = new SolidBrush(Color.FromArgb(Math.Max(1, bgOpacity * 255 / 100), customBg ?? AutoBg(light))))
                            using (GraphicsPath bp = RoundRect(new RectangleF(Z(2), Z(4), W - Z(4), H - Z(8)), Z(5)))
                                g.FillPath(bgB, bp);
                        if (hover)
                            using (SolidBrush hb = new SolidBrush(Color.FromArgb(light ? 22 : 28, fg)))
                            using (GraphicsPath hp = RoundRect(new RectangleF(Z(2), Z(4), W - Z(4), H - Z(8)), Z(5)))
                                g.FillPath(hb, hp);

                        using (SolidBrush fgB = new SolidBrush(fg))
                        using (SolidBrush dimB = new SolidBrush(dim))
                        using (Pen track = new Pen(Color.FromArgb(light ? 45 : 60, fg), penW))
                        for (int r = 0; r < rows.Count; r++)
                        {
                            float rowTop = rowsTop + r * rowH;
                            float x = pad;
                            float ty = rowTop + (rowH - (lh + vh - Z(3))) / 2f;
                            foreach (int i in rows[r])
                            {
                                if (i < 0)
                                {
                                    Item metricItem = items[-i - 1];
                                    Tps metrics = metricItem.Name == "Claude" ? claudeTps : codexTps;
                                    bool hasT = metrics.HasData;
                                    bool fresh = hasT && (DateTime.Now - metrics.LastAt).TotalMinutes < 15;
                                    Color bc = fresh ? metricItem.Accent : Color.FromArgb(light ? 90 : 110, fg);
                                    float bh = ringD, by = rowTop + (rowH - bh) / 2f;
                                    PointF[] bolt = new PointF[7];
                                    float[,] pts = { { .65f, 0f }, { .12f, .58f }, { .47f, .58f }, { .35f, 1f }, { .88f, .40f }, { .53f, .40f }, { .65f, 0f } };
                                    for (int k = 0; k < 7; k++) bolt[k] = new PointF(x + pts[k, 0] * boltW, by + pts[k, 1] * bh);
                                    using (SolidBrush bb = new SolidBrush(bc)) g.FillPolygon(bb, bolt);
                                    x += boltW + gap;

                                    string ttext = hasT ? FmtTps(metrics.Last) : "--";
                                    g.DrawString(tpsLabel, fLabel, dimB, x - Z(1), ty);
                                    g.DrawString(ttext, fValue, fresh || !hasT ? fgB : dimB, x - Z(1), ty + lh - Z(3));
                                    x += tpsW + timeGap;

                                    // Turn time like tps-meter: live while running, total once done.
                                    Tps tt = metrics;
                                    bool active = tt.TurnActive;
                                    bool turnFresh = active || (tt.HasTurn && (DateTime.Now - tt.TurnEnd).TotalMinutes < 15);
                                    string dtext = tt.HasTurn ? FmtDur((active ? DateTime.Now : tt.TurnEnd) - tt.TurnStart, active) : "--";
                                    using (SolidBrush ab = new SolidBrush(metricItem.Accent))
                                        g.DrawString(active ? "작업 중" : "작업시간", fLabel, active ? ab : dimB, x - Z(1), ty);
                                    g.DrawString(dtext, fValue, turnFresh || !tt.HasTurn ? fgB : dimB, x - Z(1), ty + lh - Z(3));
                                    x += timeW + spacing;
                                    continue;
                                }

                                Limit lim = limitMode == ModeWeek ? items[i].Data.Week : items[i].Data.Short;
                                double used = lim.EffectiveUsed(now);
                                bool has = used >= 0;
                                double remain = has ? 100 - used : 0;
                                double val = has ? (showUsed ? used : remain) : 0;
                                Color c = (has && remain <= 15) ? Color.FromArgb(229, 72, 77) : items[i].Accent;

                                RectangleF rr = new RectangleF(x + penW / 2f, rowTop + (rowH - ringD) / 2f + penW / 2f, ringD - penW, ringD - penW);
                                g.DrawEllipse(track, rr);
                                if (has && val > 0.5)
                                    using (Pen p = new Pen(c, penW))
                                    {
                                        if (val >= 99.5) g.DrawEllipse(p, rr);
                                        else
                                        {
                                            p.StartCap = LineCap.Round;
                                            p.EndCap = LineCap.Round;
                                            g.DrawArc(p, rr, -90f, (float)(360 * val / 100));
                                        }
                                    }
                                x += ringD + gap;

                                string vtext = has ? Math.Round(val).ToString(CultureInfo.InvariantCulture) + "%" : "--";
                                g.DrawString(items[i].Name, fLabel, dimB, x - Z(1), ty);
                                g.DrawString(vtext, fValue, fgB, x - Z(1), ty + lh - Z(3));
                                if (both)
                                {
                                    double wUsed = items[i].Data.Week.EffectiveUsed(now);
                                    string wtext = "주 " + (wUsed >= 0 ? Math.Round(showUsed ? wUsed : 100 - wUsed).ToString(CultureInfo.InvariantCulture) + "%" : "--");
                                    Color wc = (wUsed >= 0 && 100 - wUsed <= 15) ? Color.FromArgb(229, 72, 77) : dim;
                                    using (SolidBrush wb = new SolidBrush(wc))
                                        g.DrawString(wtext, fLabel, wb, x - Z(1) + valW + secGap, ty + lh - Z(3) + baseShift);
                                }
                                x += (vertical ? widestText : textW[i]) + spacing;
                            }
                        }
                    }
                    ApplyBitmap(bmp);
                }
            }
            UpdateTooltip(items, now);
        }

        static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        void ApplyBitmap(Bitmap bmp)
        {
            if (Width != bmp.Width || Height != bmp.Height)
            {
                // Keep the right and bottom edges in place so growing doesn't push into the tray or
                // off the screen; a drag on the right/bottom edge keeps the opposite side instead.
                int dw = bmp.Width - Width, dh = bmp.Height - Height;
                Size = new Size(bmp.Width, bmp.Height);
                if (placed && (dw != 0 || dh != 0))
                {
                    if (resizeH != 1) Left -= dw;
                    if (resizeV != 1) Top -= dh;
                    ClampToScreen();
                    if (hasSavedPos && !Resizing) SaveConfig();
                }
            }
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr old = Native.SelectObject(memDc, hBmp);
            try
            {
                Native.SIZE size = new Native.SIZE(bmp.Width, bmp.Height);
                Native.POINT src = new Native.POINT(0, 0);
                Native.POINT dst = new Native.POINT(Left, Top);
                Native.BLENDFUNCTION bf = new Native.BLENDFUNCTION();
                bf.SourceConstantAlpha = 255;
                bf.AlphaFormat = 1; // AC_SRC_ALPHA
                Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref bf, 2 /* ULW_ALPHA */);
            }
            finally
            {
                Native.SelectObject(memDc, old);
                Native.DeleteObject(hBmp);
                Native.DeleteDC(memDc);
                Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        static string Pct(double v) { return Math.Round(v).ToString(CultureInfo.InvariantCulture) + "%"; }

        // tps-meter style: "8.4s", "1m 24s"; whole seconds while ticking live.
        static string FmtDur(TimeSpan d, bool live)
        {
            double s = Math.Max(0, d.TotalSeconds);
            if (s >= 3600) return (int)(s / 3600) + "h " + (int)(s % 3600 / 60) + "m";
            if (s >= 60) return (int)(s / 60) + "m " + (int)(s % 60) + "s";
            return live ? (int)s + "s" : s.ToString("0.0", CultureInfo.InvariantCulture) + "s";
        }

        static string FmtTps(double v) { return v.ToString(v >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture); }

        static string Ago(DateTime t)
        {
            TimeSpan d = DateTime.Now - t;
            if (d.TotalSeconds < 60) return "방금";
            if (d.TotalMinutes < 60) return (int)d.TotalMinutes + "분 전";
            if (d.TotalHours < 24) return (int)d.TotalHours + "시간 전";
            return (int)d.TotalDays + "일 전";
        }

        static string FmtReset(long unix)
        {
            DateTime t = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
            CultureInfo ko = new CultureInfo("ko-KR");
            return t.Date == DateTime.Today ? t.ToString("HH:mm", ko) : t.ToString("M/d(ddd) HH:mm", ko);
        }

        static void Line(StringBuilder sb, string label, Limit l, long now)
        {
            double used = l.EffectiveUsed(now);
            if (used < 0) return;
            sb.Append("  " + label + ": " + Pct(100 - used) + " 남음 (" + Pct(used) + " 사용)");
            if (l.ResetsAt > now) sb.Append(" · 리셋 " + FmtReset(l.ResetsAt));
            else if (l.ResetsAt > 0) sb.Append(" · 리셋됨");
            sb.AppendLine();
        }

        void TpsLines(StringBuilder sb, Tps t)
        {
            if (!t.HasData)
            {
                sb.AppendLine("  출력 속도: 데이터 없음 (최근 24시간 내 응답 없음)");
            }
            else sb.AppendLine("  출력 속도: " + FmtTps(t.Last) + " t/s (" + t.LastTokens.ToString("N0", CultureInfo.InvariantCulture)
                + " 토큰 / " + t.LastSecs.ToString("0.0", CultureInfo.InvariantCulture) + "초) · " + Ago(t.LastAt));
            if (t.Avg >= 0)
                sb.AppendLine("  최근 " + Tps.AvgMinutes + "분 평균: " + FmtTps(t.Avg) + " t/s (" + t.AvgCount + "건)");
            if (t.HasTurn)
                sb.AppendLine(t.TurnActive
                    ? "  작업 중: " + FmtDur(DateTime.Now - t.TurnStart, true) + " (" + t.TurnStart.ToString("HH:mm:ss") + " 시작)"
                    : "  마지막 작업시간: " + FmtDur(t.TurnEnd - t.TurnStart, false) + " (" + t.TurnStart.ToString("HH:mm:ss") + "~" + t.TurnEnd.ToString("HH:mm:ss") + ")");
            sb.AppendLine("  * t/s는 첫 토큰 대기시간 포함 추정치, 작업시간은 도구 실행 포함");
        }

        void UpdateTooltip(Item[] items, long now)
        {
            StringBuilder sb = new StringBuilder();
            foreach (Item it in items)
            {
                Usage u = it.Data;
                sb.Append(it.LongName);
                if (u.Updated != DateTime.MinValue) sb.Append("  (갱신 " + Ago(u.Updated) + ")");
                sb.AppendLine();
                if (!u.HasData)
                {
                    sb.AppendLine(it.Name == "Claude"
                        ? "  데이터 없음 - Claude Code에 로그인되어 있는지 확인하세요"
                        : "  데이터 없음 - Codex를 한 번 사용하면 표시됩니다");
                }
                else
                {
                    Line(sb, "5시간", u.Short, now);
                    Line(sb, "주간", u.Week, now);
                }
                if (showTps) TpsLines(sb, it.Name == "Claude" ? claudeTps : codexTps);
                sb.AppendLine();
            }
            sb.Append("드래그: 이동 · 가장자리·모서리 드래그: 크기 · 우클릭: 메뉴");
            string text = sb.ToString();
            if (text != lastTip)
            {
                lastTip = text;
                tip.SetToolTip(this, text);
            }
        }

        bool ForegroundIsFullscreen()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == Handle) return false;
            StringBuilder sb = new StringBuilder(256);
            Native.GetClassName(fg, sb, 256);
            string cls = sb.ToString();
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return false;
            Native.RECT r;
            if (!Native.GetWindowRect(fg, out r)) return false;
            Screen s = Screen.FromHandle(fg);
            if (!s.Equals(Screen.FromHandle(Handle))) return false;
            return r.Left <= s.Bounds.Left && r.Top <= s.Bounds.Top && r.Right >= s.Bounds.Right && r.Bottom >= s.Bounds.Bottom;
        }

        void KeepOnTop()
        {
            bool fs = ForegroundIsFullscreen();
            if (fs && Visible) { Hide(); return; }
            if (!fs && !Visible) { Show(); Render(); }
            if (Visible && !menu.Visible)
                Native.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // TOPMOST, NOSIZE|NOMOVE|NOACTIVATE
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int eh, ev;
            HitEdge(e.Location, out eh, out ev);
            if (e.Button == MouseButtons.Left && (eh != 0 || ev != 0))
            {
                // Edge/corner drag scales the whole widget; the opposite side stays put.
                resizeH = eh;
                resizeV = ev;
                rsStartX = Cursor.Position.X;
                rsStartY = Cursor.Position.Y;
                rsStartW = Width;
                rsStartH = Height;
                rsStartZoom = zoom;
                tip.Active = false;
            }
            else if (e.Button == MouseButtons.Left)
            {
                Native.ReleaseCapture();
                Native.SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, new IntPtr(2) /* HTCAPTION */, IntPtr.Zero);
                hasSavedPos = true;
                SaveConfig();
            }
            else if (e.Button == MouseButtons.Right)
            {
                menu.Show(Cursor.Position);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (Resizing)
            {
                // Outward movement along each dragged axis; a corner is fitted so the dragged
                // corner follows the cursor (projection onto the widget's diagonal).
                float dx = resizeH != 0 ? (Cursor.Position.X - rsStartX) * resizeH : 0;
                float dy = resizeV != 0 ? (Cursor.Position.Y - rsStartY) * resizeV : 0;
                float w = resizeH != 0 ? rsStartW : 0, h = resizeV != 0 ? rsStartH : 0;
                float grow = (w * dx + h * dy) / (w * w + h * h);
                SetZoom(rsStartZoom * Math.Max(0.05f, 1 + grow));
                return;
            }
            int eh, ev;
            HitEdge(e.Location, out eh, out ev);
            Cursor = EdgeCursor(eh, ev);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!Resizing) return;
            resizeH = 0;
            resizeV = 0;
            tip.Active = true;
            hasSavedPos = true;
            SaveConfig();
            UpdateMenu();
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Render(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Render(); }
    }

    // Small "turn finished" card shown next to the widget. Never takes focus; click or wait to dismiss.
    public class ToastForm : Form
    {
        readonly string head, title, sub;
        readonly Color fg, dim, accent, border;
        readonly Font fHead, fTitle, fSub;
        readonly int pad, bar, radius;
        readonly System.Windows.Forms.Timer life, blinkTimer;
        int blinksLeft; // remaining on/off toggles
        bool flashOn;

        public ToastForm(string head, string title, string sub, Color bg, Color fg, Color accent, float px, int seconds, int blinks)
        {
            Text = head;
            this.head = head;
            this.title = title;
            this.sub = sub;
            this.fg = fg;
            this.accent = accent;
            dim = Color.FromArgb(170, fg);
            border = Color.FromArgb(50, fg);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = bg;
            DoubleBuffered = true;
            Cursor = Cursors.Hand;

            fHead = new Font("Segoe UI", 11f * px, FontStyle.Bold, GraphicsUnit.Pixel);
            fTitle = new Font("Segoe UI", 13f * px, FontStyle.Regular, GraphicsUnit.Pixel);
            fSub = new Font("Segoe UI", 11f * px, FontStyle.Regular, GraphicsUnit.Pixel);
            pad = (int)Math.Round(10 * px);
            bar = Math.Max(3, (int)Math.Round(4 * px));
            radius = (int)Math.Round(8 * px);

            int textW;
            using (Graphics g = CreateGraphics())
            {
                textW = (int)Math.Ceiling(Math.Max(g.MeasureString(head, fHead).Width,
                    Math.Max(g.MeasureString(title, fTitle).Width, g.MeasureString(sub, fSub).Width)));
            }
            int w = Math.Max((int)(220 * px), Math.Min((int)(380 * px), textW + bar + pad * 2 + (int)(4 * px)));
            int h = pad * 2 + fHead.Height + fTitle.Height + fSub.Height + (int)(2 * px);
            Size = new Size(w, h);
            using (GraphicsPath gp = Round(new Rectangle(0, 0, w, h), radius)) Region = new Region(gp);

            Click += delegate { Close(); };
            life = new System.Windows.Forms.Timer();
            life.Interval = seconds * 1000;
            life.Tick += delegate { Close(); };
            life.Start();

            // Pulse an accent tint a few times so the card catches the eye.
            blinkTimer = new System.Windows.Forms.Timer();
            blinkTimer.Interval = 280;
            blinkTimer.Tick += delegate
            {
                flashOn = !flashOn;
                Invalidate();
                if (--blinksLeft <= 0) { flashOn = false; blinkTimer.Stop(); Invalidate(); }
            };
            if (blinks > 0)
            {
                blinksLeft = blinks * 2;
                flashOn = true;
                blinkTimer.Start();
            }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x08000000 | 0x8; // toolwindow | noactivate | topmost
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        static GraphicsPath Round(Rectangle r, int rad)
        {
            GraphicsPath p = new GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d - 1, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d - 1, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            if (flashOn)
                using (SolidBrush tint = new SolidBrush(Color.FromArgb(80, accent)))
                    g.FillRectangle(tint, ClientRectangle);
            using (SolidBrush ab = new SolidBrush(accent)) g.FillRectangle(ab, 0, 0, bar, Height);
            using (Pen bp = flashOn ? new Pen(accent, Math.Max(2, bar / 2)) : new Pen(border))
            using (GraphicsPath gp = Round(new Rectangle(0, 0, Width, Height), radius))
                g.DrawPath(bp, gp);

            float x = bar + pad, y = pad, w = Width - x - pad;
            using (StringFormat sf = new StringFormat(StringFormatFlags.NoWrap))
            using (SolidBrush ab = new SolidBrush(accent))
            using (SolidBrush fb = new SolidBrush(fg))
            using (SolidBrush db = new SolidBrush(dim))
            {
                sf.Trimming = StringTrimming.EllipsisCharacter;
                g.DrawString(head, fHead, ab, new RectangleF(x, y, w, fHead.Height), sf);
                y += fHead.Height + 1;
                g.DrawString(title, fTitle, fb, new RectangleF(x, y, w, fTitle.Height), sf);
                y += fTitle.Height + 1;
                g.DrawString(sub, fSub, db, new RectangleF(x, y, w, fSub.Height), sf);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                life.Dispose();
                blinkTimer.Dispose();
                fHead.Dispose();
                fTitle.Dispose();
                fSub.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    public static class App
    {
        [STAThread]
        public static int Main(string[] args)
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            if (args.Length > 0 && args[0] == "-test")
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                Usage c = Sources.ReadCodex(Path.Combine(home, ".codex", "sessions"));
                Usage a = Sources.ReadClaude(Path.Combine(dir, "claude-usage.json"));
                Console.WriteLine("codex  5h=" + c.Short.UsedPct + " (win " + c.Short.WindowMin + "m) week=" + c.Week.UsedPct + " updated=" + c.Updated);
                Console.WriteLine("claude 5h=" + a.Short.UsedPct + " week=" + a.Week.UsedPct + " updated=" + a.Updated);
                Usage api = Sources.ReadClaudeApi(Path.Combine(home, ".claude", ".credentials.json"));
                Console.WriteLine(api == null ? "claude api: failed" : "claude api 5h=" + api.Short.UsedPct + " reset=" + api.Short.ResetsAt
                    + " week=" + api.Week.UsedPct + " reset=" + api.Week.ResetsAt);
                Tps t = Sources.ReadClaudeTps(Path.Combine(home, ".claude", "projects"));
                Console.WriteLine("claude tps last=" + t.Last.ToString("0.0") + " (" + t.LastTokens + " tok / " + t.LastSecs.ToString("0.0") + "s at " + t.LastAt
                    + ") avg" + Tps.AvgMinutes + "m=" + t.Avg.ToString("0.0") + " n=" + t.AvgCount);
                Console.WriteLine("claude turn start=" + t.TurnStart + " end=" + t.TurnEnd + " active=" + t.TurnActive);
                foreach (SessionTurn s in t.Sessions)
                    Console.WriteLine("  session title=" + s.Title + " project=" + s.Project + " start=" + s.Start.ToString("HH:mm:ss")
                        + " end=" + (s.End == DateTime.MinValue ? "running" : s.End.ToString("HH:mm:ss")) + " interrupted=" + s.Interrupted + " abandoned=" + s.Abandoned);
                return 0;
            }
            WidgetForm.PreviewOnStart = args.Length > 0 && args[0] == "-preview";
            Run(dir);
            return 0;
        }

        public static void Run(string dataDir)
        {
            bool created;
            using (Mutex m = new Mutex(true, "Local\\UsageWidgetClaudeCodex", out created))
            {
                if (!created) return;
                try { Native.SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.Run(new WidgetForm(dataDir));
            }
        }
    }
}
