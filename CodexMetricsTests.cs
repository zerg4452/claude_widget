using System;
using System.IO;
using System.Text;
using System.Globalization;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using UsageWidget;

public static class CodexMetricsTests
{
    static JavaScriptSerializer json = new JavaScriptSerializer();
    static DateTime begin = DateTime.UtcNow.AddMinutes(-2);
    static int passed;
    static string Event(int seconds, string kind, object payload)
    {
        return json.Serialize(new { timestamp = begin.AddSeconds(seconds).ToString("o"), type = kind, payload = payload }) + "\n";
    }
    static string Tokens(int seconds, long total, long last)
    {
        return Event(seconds, "event_msg", new { type = "token_count", info = new { total_token_usage = new { output_tokens = total }, last_token_usage = new { output_tokens = last } } });
    }
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
        passed++;
    }
    static string NewLog(string root, string name)
    {
        string folder = Path.Combine(root, "2026", "10", "12");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, name + ".jsonl");
    }
    public static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "usage-widget-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Check(!Sources.ReadCodexTps(root).HasData, "missing logs");
            string f = NewLog(root, "main");
            string log = Event(0, "session_meta", new { source = "vscode" })
                + Event(0, "event_msg", new { type = "task_started", turn_id = "a" })
                + Event(10, "response_item", new { type = "custom_tool_call" })
                + Event(50, "response_item", new { type = "custom_tool_call_output", output = "fake task_complete token_count 한글" })
                + Tokens(50, 100, 100) + Tokens(51, 100, 100)
                + Event(60, "response_item", new { type = "message", role = "assistant", phase = "final_answer" })
                + Tokens(60, 300, 200)
                + Event(61, "event_msg", new { type = "task_complete", turn_id = "a" });
            File.WriteAllText(f, log, new UTF8Encoding(false));
            Tps t = Sources.ReadCodexTps(root);
            Check(t.LastTokens == 200 && Math.Abs(t.LastSecs - 10) < .001 && Math.Abs(t.Last - 20) < .001, "tool wait excluded from speed");
            Check(t.AvgCount == 2 && Math.Abs(t.Avg - 15) < .001, "duplicate token snapshots ignored; weighted average");
            Check(!t.TurnActive && (t.TurnEnd - t.TurnStart).TotalSeconds == 61, "completed duration includes tools");
            Check(t.Sessions.Count == 1 && t.Sessions[0].AI == "Codex", "Codex completion supplied to notifications");
            SessionTurn completed = t.Sessions[0];
            HashSet<string> notified = new HashSet<string>();
            Check(WidgetForm.TryMarkNotification(completed, begin.AddSeconds(-1).ToLocalTime(), begin.AddSeconds(62).ToLocalTime(), notified), "completed Codex turn notifies");
            Check(!WidgetForm.TryMarkNotification(completed, begin.AddSeconds(-1).ToLocalTime(), begin.AddSeconds(63).ToLocalTime(), notified), "same completion notifies once");
            Check(!WidgetForm.TryMarkNotification(completed, begin.AddSeconds(62).ToLocalTime(), begin.AddSeconds(63).ToLocalTime(), new HashSet<string>()), "prelaunch completion excluded");
            Check(!WidgetForm.TryMarkNotification(completed, begin.AddSeconds(-1).ToLocalTime(), begin.AddMinutes(7).ToLocalTime(), new HashSet<string>()), "old completion excluded");
            Check(Sources.ReadCodexTps(root).AvgCount == 2, "cached poll does not duplicate samples");
            File.AppendAllText(f, Event(70, "event_msg", new { type = "task_started", turn_id = "b" }), new UTF8Encoding(false));
            t = Sources.ReadCodexTps(root);
            Check(t.TurnActive && t.TurnStart.ToUniversalTime() == begin.AddSeconds(70), "appended new turn starts clock");
            Check(t.Sessions.Exists(s => s.Start == completed.Start && s.End == completed.End), "completion survives next turn between polls");
            Check(!WidgetForm.TryMarkNotification(t.Sessions.Find(s => s.End == DateTime.MinValue), begin.AddSeconds(-1).ToLocalTime(), DateTime.Now, new HashSet<string>()), "running turn excluded");
            File.AppendAllText(f, Event(71, "event_msg", new { type = "task_complete", turn_id = "a" }), new UTF8Encoding(false));
            Check(Sources.ReadCodexTps(root).TurnActive, "old completion cannot end new turn");
            string partial = Event(80, "event_msg", new { type = "turn_aborted", turn_id = "b", note = "한글" });
            byte[] bytes = Encoding.UTF8.GetBytes(partial);
            int split = Array.IndexOf(bytes, (byte)0xed) + 1;
            using (FileStream fs = new FileStream(f, FileMode.Append)) fs.Write(bytes, 0, split);
            Check(Sources.ReadCodexTps(root).TurnActive, "incomplete UTF-8 JSON deferred");
            using (FileStream fs = new FileStream(f, FileMode.Append)) fs.Write(bytes, split, bytes.Length - split);
            t = Sources.ReadCodexTps(root);
            Check(!t.TurnActive && (t.TurnEnd - t.TurnStart).TotalSeconds == 10, "completed partial abort stops clock");
            SessionTurn aborted = t.Sessions.Find(s => s.Start.ToUniversalTime() == begin.AddSeconds(70));
            Check(aborted.Interrupted && !WidgetForm.TryMarkNotification(aborted, begin.ToLocalTime(), DateTime.Now, new HashSet<string>()), "aborted turn never notifies");
            string sub = NewLog(root, "guardian");
            File.WriteAllText(sub, Event(90, "session_meta", new { source = new { subagent = new { other = "guardian" } } })
                + Event(90, "event_msg", new { type = "task_started", turn_id = "guardian" }) + Tokens(100, 9999, 9999));
            t = Sources.ReadCodexTps(root);
            Check(t.TurnStart.ToUniversalTime() == begin.AddSeconds(70) && t.LastTokens == 200, "guardian session excluded");
            Check(!t.Sessions.Exists(s => s.Path == sub), "guardian cannot notify");
            File.WriteAllText(f, Event(90, "event_msg", new { type = "task_started", turn_id = "c" }) + "not json\n" + Tokens(100, 50, 50));
            t = Sources.ReadCodexTps(root);
            Check(t.TurnActive && t.LastTokens == 50, "truncation resets cache; malformed line skipped");
            File.AppendAllText(f, Event(105, "response_item", new { type = "message", role = "assistant" }) + Tokens(105, 10, 10));
            Check(Sources.ReadCodexTps(root).LastTokens == 50, "counter reset does not create false speed");
            string active = NewLog(root, "active");
            File.WriteAllText(active, Event(85, "event_msg", new { type = "task_started", turn_id = "active" }));
            File.AppendAllText(f, Event(106, "event_msg", new { type = "task_complete", turn_id = "c" }));
            Check(Sources.ReadCodexTps(root).TurnStart.ToUniversalTime() == begin.AddSeconds(85), "running main session takes priority");
            string staleRoot = root + "-stale", stale = NewLog(staleRoot, "stale");
            File.WriteAllText(stale, Event(-3600, "event_msg", new { type = "task_started" }));
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddMinutes(-40));
            Check(!Sources.ReadCodexTps(staleRoot).TurnActive, "abandoned session clock stops");
            Directory.Delete(staleRoot, true);
            staleRoot = root + "-numeric";
            stale = NewLog(staleRoot, "numeric");
            long unix = new DateTimeOffset(begin).ToUnixTimeSeconds();
            File.WriteAllText(stale, Event(0, "event_msg", new { type = "task_started", started_at = unix })
                + Event(20, "event_msg", new { type = "task_complete", completed_at = unix + 15 }));
            t = Sources.ReadCodexTps(staleRoot);
            Check((t.TurnEnd - t.TurnStart).TotalSeconds == 15, "numeric recorded start and completion times");
            Directory.Delete(staleRoot, true);
            staleRoot = root + "-early-token";
            stale = NewLog(staleRoot, "early-token");
            File.WriteAllText(stale, Event(0, "event_msg", new { type = "task_started" })
                + Event(10, "response_item", new { type = "function_call" }) + Tokens(10, 100, 100)
                + Event(50, "response_item", new { type = "function_call_output" })
                + Event(60, "response_item", new { type = "message", role = "assistant" }) + Tokens(60, 300, 200));
            t = Sources.ReadCodexTps(staleRoot);
            Check(t.LastTokens == 200 && Math.Abs(t.LastSecs - 10) < .001, "tool output after token count resets next request anchor");
            Directory.Delete(staleRoot, true);
            staleRoot = root + "-titles";
            stale = NewLog(staleRoot, "titles");
            File.WriteAllText(stale, Event(0, "event_msg", new { type = "task_started", turn_id = "title1" })
                + Event(1, "response_item", new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "# AGENTS.md instructions for design_refs" } }, internal_chat_message_metadata_passthrough = new { turn_id = "title1", content_item_kinds = new[] { "agents_md.instructions" } } })
                + Event(2, "response_item", new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "<external_codex_apps_open_page>page</external_codex_apps_open_page>" } } })
                + Event(3, "response_item", new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "알림확인용\n메시지" } }, internal_chat_message_metadata_passthrough = new { turn_id = "title1", content_item_kinds = new[] { "user.text" } } })
                + Event(4, "response_item", new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "<send_user_message_question_reply>reply</send_user_message_question_reply>" } } })
                + Event(5, "event_msg", new { type = "task_complete", turn_id = "title1" })
                + Event(6, "event_msg", new { type = "task_started", turn_id = "title2" })
                + Event(7, "response_item", new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "과거 메시지" } }, internal_chat_message_metadata_passthrough = new { turn_id = "title1", content_item_kinds = new[] { "user.text" } } })
                + Event(8, "response_item", new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "다음 작업 요청" } } })
                + Event(9, "event_msg", new { type = "task_complete", turn_id = "title2" }));
            t = Sources.ReadCodexTps(staleRoot);
            Check(t.Sessions[0].Title == "알림확인용 메시지", "desktop user content supplies title; injected context ignored");
            Check(t.Sessions[1].Title == "다음 작업 요청", "title resets per turn; earlier turn content ignored");
            Directory.Delete(staleRoot, true);
                string nameHome = Path.Combine(root, "named-home"), nameLogs = Path.Combine(nameHome, "sessions");
                string named = NewLog(nameLogs, "named");
                File.WriteAllText(named, Event(0, "session_meta", new { id = "session-one", source = "vscode" })
                    + Event(0, "event_msg", new { type = "task_started" })
                    + Event(1, "event_msg", new { type = "user_message", message = "현재 요청 내용" })
                    + Event(2, "event_msg", new { type = "task_complete" }));
                string index = Path.Combine(nameHome, "session_index.jsonl");
                File.WriteAllText(index, "bad json\n" + json.Serialize(new { id = "session-one", thread_name = "프로젝트의 세션명" }) + "\n");
                t = Sources.ReadCodexTps(nameLogs);
                Check(t.Sessions[0].SessionTitle == "프로젝트의 세션명" && t.Sessions[0].Title == "현재 요청 내용", "session index supplies sidebar title independently of prompt");
                File.AppendAllText(index, json.Serialize(new { id = "session-one", thread_name = "바꾼 세션명" }) + "\n");
                Check(Sources.ReadCodexTps(nameLogs).Sessions[0].SessionTitle == "바꾼 세션명", "rename reflected even when transcript is cached");
                File.Delete(index);
                Check(Sources.ReadCodexTps(nameLogs).Sessions[0].SessionTitle == null, "missing index clears stale sidebar title");
            if (args.Length > 0)
            {
                t = Sources.ReadCodexTps(args[0]);
                Check(t.HasData && t.HasTurn, "real Codex logs yield speed and turn");
                Check(t.Sessions.Exists(s => !string.IsNullOrEmpty(s.Title)), "real desktop logs yield notification title");
                Check(t.Sessions.Exists(s => !string.IsNullOrEmpty(s.SessionTitle)), "real session index yields sidebar title");
                Console.WriteLine("LIVE: t/s=" + t.Last.ToString("0.00", CultureInfo.InvariantCulture) + " tokens=" + t.LastTokens
                    + " seconds=" + t.LastSecs.ToString("0.000", CultureInfo.InvariantCulture)
                    + " active=" + t.TurnActive + " turnStart=" + t.TurnStart.ToString("o"));
                var watch = System.Diagnostics.Stopwatch.StartNew();
                Sources.ReadCodexTps(args[0]);
                Console.WriteLine("Cached poll ms=" + watch.ElapsedMilliseconds);
            }
            Console.WriteLine("Passed " + passed + " checks.");
            return 0;
        }
        catch (Exception e) { Console.WriteLine(e.Message); return 1; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
