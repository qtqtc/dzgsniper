using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

class Program
{
    const string Chars = "abcdefghijklmnopqrstuvwxyz0123456789_";
    const int NamesPerSecond = 10;
    const int BatchSize = 10;
    const int MinRequestIntervalMs = 1000;
    const int MaxRequestIntervalMs = 6000;
    const int RateLimitWaitSec = 10;
    const int ErrorWaitSec = 3;
    const int BufferTarget = 40;
    const string BulkUrl = "https://api.minecraftservices.com/minecraft/profile/lookup/bulk/byname";
    const string AvailUrl = "https://api.minecraftservices.com/minecraft/profile/name/{0}/available";

    const int StAvailable = 0, StTaken = 1, StBlocked = 2, StNotAllowed = 3, StUnknown = 4;

    struct Result { public string Name; public int Status; }

    static Random rng = new Random(Guid.NewGuid().GetHashCode());
    static Regex nameRegex = new Regex("\"name\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);
    static Regex statusRegex = new Regex("\"status\"\\s*:\\s*\"([A-Z_]+)\"", RegexOptions.Compiled);
    static object consoleLock = new object();
    static Queue<Result> results = new Queue<Result>();
    static int mode = 3;
    static int requestIntervalMs = MinRequestIntervalMs;
    static string token = null;

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetConsoleMode(IntPtr h, uint mode);

    static void DisableQuickEdit()
    {
        try
        {
            IntPtr h = GetStdHandle(-10);
            uint m;
            if (GetConsoleMode(h, out m))
            {
                m &= ~0x0040u;
                m |= 0x0080u;
                SetConsoleMode(h, m);
            }
        }
        catch { }
    }

    static HttpWebRequest NewRequest(string url, string method)
    {
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
        req.Method = method;
        req.Proxy = null;
        req.Accept = "application/json";
        req.UserAgent = "dzgsniper";
        req.Timeout = 8000;
        req.ReadWriteTimeout = 8000;
        req.KeepAlive = true;
        return req;
    }

    static HttpWebResponse Send(HttpWebRequest req)
    {
        try { return (HttpWebResponse)req.GetResponse(); }
        catch (WebException ex)
        {
            if (ex.Response == null) return null;
            return (HttpWebResponse)ex.Response;
        }
    }

    static string ReadBody(HttpWebResponse resp)
    {
        using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            return sr.ReadToEnd();
    }

    static int CheckBatch(List<string> names, HashSet<string> taken)
    {
        try
        {
            StringBuilder body = new StringBuilder("[");
            for (int i = 0; i < names.Count; i++)
            {
                if (i > 0) body.Append(',');
                body.Append('"').Append(names[i]).Append('"');
            }
            body.Append(']');
            byte[] data = Encoding.UTF8.GetBytes(body.ToString());

            HttpWebRequest req = NewRequest(BulkUrl, "POST");
            req.ContentType = "application/json";
            req.ContentLength = data.Length;
            using (Stream s = req.GetRequestStream())
                s.Write(data, 0, data.Length);

            HttpWebResponse resp = Send(req);
            if (resp == null) return 0;
            using (resp)
            {
                int code = (int)resp.StatusCode;
                if (code != 200) return code;
                foreach (Match m in nameRegex.Matches(ReadBody(resp)))
                    taken.Add(m.Groups[1].Value.ToLowerInvariant());
                return 200;
            }
        }
        catch { return 0; }
    }

    static int CheckAvailable(string name, out int status)
    {
        status = StUnknown;
        try
        {
            HttpWebRequest req = NewRequest(string.Format(AvailUrl, name), "GET");
            req.Headers["Authorization"] = "Bearer " + token;
            HttpWebResponse resp = Send(req);
            if (resp == null) return 0;
            using (resp)
            {
                int code = (int)resp.StatusCode;
                if (code != 200) return code;
                Match m = statusRegex.Match(ReadBody(resp));
                string s = m.Success ? m.Groups[1].Value : "";
                if (s == "AVAILABLE") status = StAvailable;
                else if (s == "DUPLICATE") status = StBlocked;
                else if (s == "NOT_ALLOWED") status = StNotAllowed;
                return 200;
            }
        }
        catch { return 0; }
    }

    static string RandomName(int len)
    {
        StringBuilder sb = new StringBuilder(len);
        for (int i = 0; i < len; i++)
            sb.Append(Chars[rng.Next(Chars.Length)]);
        return sb.ToString();
    }

    static List<string> MakeBatch()
    {
        List<string> batch = new List<string>(BatchSize);
        HashSet<string> seen = new HashSet<string>();
        while (batch.Count < BatchSize)
        {
            int len;
            if (mode == 1) len = 3;
            else if (mode == 2) len = 4;
            else len = rng.Next(2) == 0 ? 3 : 4;
            string name = RandomName(len);
            if (seen.Add(name)) batch.Add(name);
        }
        return batch;
    }

    static void Line(ConsoleColor color, string text)
    {
        lock (consoleLock)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ResetColor();
        }
    }

    static int Buffered()
    {
        lock (results) return results.Count;
    }

    static void Enqueue(string name, int status)
    {
        Result r;
        r.Name = name;
        r.Status = status;
        lock (results) results.Enqueue(r);
    }

    static int Verify(string name)
    {
        while (true)
        {
            int status;
            int code = CheckAvailable(name, out status);
            if (code == 200) return status;
            if (code == 429)
            {
                Line(ConsoleColor.Yellow, " [!] rate limited on availability check - retrying in " + RateLimitWaitSec + "s");
                Thread.Sleep(RateLimitWaitSec * 1000);
                continue;
            }
            if (code == 401 || code == 403)
            {
                Line(ConsoleColor.Yellow, " [!] token rejected (HTTP " + code + ") - cooldown / not-allowed detection off");
                token = null;
                return StAvailable;
            }
            Line(ConsoleColor.DarkYellow, " [?] availability check failed (" + (code == 0 ? "no response" : "HTTP " + code) + ") - retrying in " + ErrorWaitSec + "s");
            Thread.Sleep(ErrorWaitSec * 1000);
        }
    }

    static void Fetcher()
    {
        Stopwatch sw = new Stopwatch();
        int okStreak = 0;

        while (true)
        {
            if (Buffered() >= BufferTarget)
            {
                Thread.Sleep(50);
                continue;
            }

            List<string> batch = MakeBatch();
            while (true)
            {
                sw.Restart();
                HashSet<string> taken = new HashSet<string>();
                int code = CheckBatch(batch, taken);

                if (code == 200)
                {
                    foreach (string n in batch)
                    {
                        if (taken.Contains(n.ToLowerInvariant()))
                            Enqueue(n, StTaken);
                        else if (token != null)
                            Enqueue(n, Verify(n));
                        else
                            Enqueue(n, StAvailable);
                    }
                    okStreak++;
                    if (okStreak >= 15 && requestIntervalMs > MinRequestIntervalMs)
                    {
                        requestIntervalMs = Math.Max(MinRequestIntervalMs, requestIntervalMs - 250);
                        okStreak = 0;
                    }
                    break;
                }
                else if (code == 429)
                {
                    okStreak = 0;
                    requestIntervalMs = Math.Min(MaxRequestIntervalMs, requestIntervalMs + 500);
                    Line(ConsoleColor.Yellow, " [!] rate limited by Mojang - retrying in " + RateLimitWaitSec + "s (slowing to 1 request / " + (requestIntervalMs / 1000.0).ToString("0.0") + "s)");
                    Thread.Sleep(RateLimitWaitSec * 1000);
                }
                else
                {
                    okStreak = 0;
                    Line(ConsoleColor.DarkYellow, " [?] request failed (" + (code == 0 ? "no response" : "HTTP " + code) + ") - retrying in " + ErrorWaitSec + "s");
                    Thread.Sleep(ErrorWaitSec * 1000);
                }
            }

            int remaining = requestIntervalMs - (int)sw.ElapsedMilliseconds;
            if (remaining > 0) Thread.Sleep(remaining);
        }
    }

    static string ReadHidden()
    {
        StringBuilder sb = new StringBuilder();
        while (true)
        {
            ConsoleKeyInfo k = Console.ReadKey(true);
            if (k.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (k.KeyChar != '\0') sb.Append(k.KeyChar);
        }
        return sb.ToString().Trim();
    }

    static void LoadToken()
    {
        try
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "token.txt");
            if (File.Exists(path))
            {
                string t = File.ReadAllText(path).Trim();
                if (t.Length > 0) { token = t; Line(ConsoleColor.DarkGray, " token loaded from token.txt"); return; }
            }
        }
        catch { }

        Console.Write(" minecraft token (Enter to skip): ");
        string typed;
        try { typed = ReadHidden(); } catch { typed = (Console.ReadLine() ?? "").Trim(); }
        if (typed.Length > 0) token = typed;
    }

    static void Banner()
    {
        try { Console.Clear(); } catch { }
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  ____  _____ ____ ____  _   _ ___ ____  _____ ____  ");
        Console.WriteLine(" |  _ \\|__  // ___/ ___|| \\ | |_ _|  _ \\| ____|  _ \\ ");
        Console.WriteLine(" | | | | / /| |  _\\___ \\|  \\| || || |_) |  _| | |_) |");
        Console.WriteLine(" | |_| |/ /_| |_| |___) | |\\  || ||  __/| |___|  _ < ");
        Console.WriteLine(" |____//____|\\____|____/|_| \\_|___|_|   |_____|_| \\_\\");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("                 made by dzg  |  Mojang checker");
        Console.ResetColor();
        Console.WriteLine();
    }

    static void Main()
    {
        Console.Title = "dzgsniper - made by dzg";
        DisableQuickEdit();
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        ServicePointManager.Expect100Continue = false;
        ServicePointManager.DefaultConnectionLimit = 10;

        Banner();
        Console.WriteLine(" [1] 3 letter names");
        Console.WriteLine(" [2] 4 letter names");
        Console.WriteLine(" [3] both");
        Console.WriteLine();

        mode = 0;
        while (mode == 0)
        {
            Console.Write(" dzgsniper> ");
            string input = (Console.ReadLine() ?? "").Trim();
            if (input == "1") mode = 1;
            else if (input == "2") mode = 2;
            else if (input == "3") mode = 3;
            else Line(ConsoleColor.Red, " pick 1, 2 or 3");
        }

        LoadToken();

        Console.WriteLine();
        Line(ConsoleColor.DarkGray, " [+] available  [-] taken  [~] blocked/cooldown  [x] not allowed");
        if (token == null)
            Line(ConsoleColor.DarkGray, " no token: cooldown / not-allowed names will show as [+]");
        Line(ConsoleColor.DarkGray, " checking " + NamesPerSecond + " names/sec... press Ctrl+C to stop");
        Console.WriteLine();

        Thread fetcher = new Thread(Fetcher);
        fetcher.IsBackground = true;
        fetcher.Start();

        int printIntervalMs = 1000 / NamesPerSecond;
        Stopwatch clock = Stopwatch.StartNew();
        long next = 0;
        while (true)
        {
            Result r;
            bool have = false;
            lock (results)
            {
                if (results.Count > 0) { r = results.Dequeue(); have = true; }
                else r = new Result();
            }

            if (have)
            {
                switch (r.Status)
                {
                    case StTaken:      Line(ConsoleColor.Red, " [-]" + r.Name); break;
                    case StBlocked:    Line(ConsoleColor.DarkYellow, " [~]" + r.Name + "  (blocked / cooldown)"); break;
                    case StNotAllowed: Line(ConsoleColor.Magenta, " [x]" + r.Name + "  (not allowed)"); break;
                    case StAvailable:  Line(ConsoleColor.Green, " [+]" + r.Name); break;
                    default:           Line(ConsoleColor.DarkYellow, " [?]" + r.Name + "  (unknown)"); break;
                }
                next += printIntervalMs;
                long wait = next - clock.ElapsedMilliseconds;
                if (wait > 0) Thread.Sleep((int)wait);
            }
            else
            {
                Thread.Sleep(25);
                next = clock.ElapsedMilliseconds;
            }
        }
    }
}
