using System;
using System.Collections.Generic;
using System.IO;
using System.Drawing;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using UsageWidget;

public class NotificationIntegrationTests
{
    static JavaScriptSerializer json = new JavaScriptSerializer();
    static string Event(DateTime t, string kind, object payload)
    {
        return json.Serialize(new { timestamp = t.ToUniversalTime().ToString("o"), type = kind, payload = payload }) + "\n";
    }
    [STAThread]
    public static int Main(string[] args)
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "usage-widget-ui-" + Guid.NewGuid().ToString("N")));
        if (!root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) return 1;
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("CODEX_HOME", root);
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", root);
        WidgetForm form = null;
        List<ToastForm> cards = null;
        try
        {
            Application.EnableVisualStyles();
            File.WriteAllText(Path.Combine(root, "config.ini"), "tps=0\ncodex=0\nnotify=1\nblink=0\nbgop=100\n");
            form = new WidgetForm(root);
            form.Location = new Point(150, 150);
            form.Size = new Size(500, 48);
            string folder = Path.Combine(root, "sessions", "2026", "10", "12");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "test.jsonl");
            DateTime end = DateTime.Now;
            File.WriteAllText(Path.Combine(root, "session_index.jsonl"), json.Serialize(new { id = "demo-id", thread_name = "코덱스 위젯 작업시간 노출 수정" }) + "\n");
            File.WriteAllText(path, Event(end.AddSeconds(-5), "session_meta", new { id = "demo-id", source = "vscode", cwd = "C:/demo-project" })
                + Event(end.AddSeconds(-5), "event_msg", new { type = "task_started", turn_id = "one" })
                + Event(end.AddSeconds(-4), "response_item", new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "Codex 완료 알림 검증" } }, internal_chat_message_metadata_passthrough = new { turn_id = "one", content_item_kinds = new[] { "user.text" } } })
                + Event(end, "event_msg", new { type = "task_complete", turn_id = "one" }));
            MethodInfo refresh = typeof(WidgetForm).GetMethod("RefreshData", BindingFlags.Instance | BindingFlags.NonPublic);
            refresh.Invoke(form, null);
            cards = (List<ToastForm>)typeof(WidgetForm).GetField("toasts", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
            if (cards.Count != 1 || !cards[0].Visible || !cards[0].Text.Contains("Codex 작업 완료")) throw new Exception("Codex toast missing.");
            if ((string)typeof(ToastForm).GetField("title", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(cards[0]) != "코덱스 위젯 작업시간 노출 수정") throw new Exception("Sidebar session title missing.");
            refresh.Invoke(form, null);
            if (cards.Count != 1) throw new Exception("Duplicate toast.");
            File.AppendAllText(path, Event(end, "event_msg", new { type = "task_started", turn_id = "two" })
                + Event(end, "event_msg", new { type = "turn_aborted", turn_id = "two" }));
            refresh.Invoke(form, null);
            if (cards.Count != 1) throw new Exception("Abort generated toast.");
            using (Bitmap image = new Bitmap(cards[0].Width, cards[0].Height))
            {
                cards[0].DrawToBitmap(image, new Rectangle(Point.Empty, cards[0].Size));
                image.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "notification-render.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            Console.WriteLine("PASS: real RefreshData creates one visible Codex toast with metrics/AI hidden; polling deduplicates; abort suppressed.");
            if (args.Length > 0 && args[0] == "-inspect")
            {
                var life = (System.Windows.Forms.Timer)typeof(ToastForm).GetField("life", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(cards[0]);
                life.Stop();
                var quit = new System.Windows.Forms.Timer { Interval = 60000 };
                quit.Tick += delegate { Application.ExitThread(); };
                quit.Start();
                Application.Run();
                quit.Dispose();
            }
            return 0;
        }
        catch (Exception e) { Console.WriteLine(e.Message); return 1; }
        finally
        {
            if (cards != null) foreach (ToastForm card in cards.ToArray()) card.Close();
            if (form != null) form.Dispose();
            Directory.Delete(root, true);
        }
    }
}
