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
    const int BatchSize = 10;
    const int Workers = 12;
    const int BurstIntervalMs = 100;
    const int StableIntervalMs = 500;
    const int MaxRequestIntervalMs = 6000;
    const int RateLimitWaitSec = 10;
    const int ErrorWaitSec = 3;
    const int BufferTarget = 300;
    const string BulkUrl = "https://api.minecraftservices.com/minecraft/profile/lookup/bulk/byname";

    struct Result { public string Name; public bool Taken; }

    static readonly string[] OgWords = new string[] {
        "Shadow","Wolf","Dragon","Phoenix","Ghost","Storm","Blaze","Frost","Night","Dark",
        "Light","Fire","Ice","Water","Earth","Wind","Sky","Star","Moon","Sun",
        "Apple","Banana","Cherry","Grape","Lemon","Mango","Peach","Berry","Melon","Kiwi",
        "Tiger","Lion","Bear","Fox","Hawk","Eagle","Raven","Crow","Owl","Falcon",
        "Shark","Whale","Dolphin","Snake","Viper","Cobra","Panda","Koala","Otter","Lynx",
        "Red","Blue","Green","Black","White","Gold","Silver","Purple","Pink","Gray",
        "King","Queen","Prince","Knight","Wizard","Mage","Rogue","Ninja","Samurai","Pirate",
        "Hunter","Sniper","Ranger","Archer","Reaper","Slayer","Warrior","Titan","Giant","Hero",
        "Alpha","Beta","Omega","Delta","Sigma","Zeta","Nova","Vortex","Pulse","Echo",
        "Jack","John","James","Mike","Alex","Chris","David","Daniel","Ryan","Kevin",
        "Emma","Sarah","Anna","Lily","Rose","Grace","Luna","Mia","Zoe","Ava",
        "Lucky","Happy","Crazy","Lazy","Sleepy","Angry","Silent","Quiet","Loud","Wild",
        "Candy","Cookie","Pizza","Taco","Sushi","Donut","Waffle","Bacon","Toast","Honey",
        "Pixel","Byte","Code","Glitch","Lag","Ping","Hack","Bug","Chip","Data",
        "Dream","Hope","Faith","Love","Peace","Soul","Mind","Heart","Spirit","Fate",
        "Ocean","River","Lake","Forest","Desert","Island","Canyon","Valley","Cliff","Cave",
        "Thunder","Lightning","Tornado","Blizzard","Hurricane","Cyclone","Comet","Meteor","Galaxy","Cosmos",
        "Sword","Shield","Arrow","Blade","Dagger","Axe","Hammer","Spear","Bow","Lance",
        "Smoke","Flame","Spark","Ember","Ash","Dust","Mist","Fog","Cloud","Rain",
        "Jade","Ruby","Onyx","Opal","Pearl","Amber","Topaz","Quartz","Coral","Ivory",
        "Max","Sam","Ben","Tom","Leo","Eli","Ian","Kai","Zed","Rex",
        "Boss","Chief","Lord","Duke","Baron","Count","Sage","Monk","Saint","Angel",
        "Demon","Devil","Beast","Monster","Zombie","Vampire","Witch","Golem","Troll","Orc",
        "Steve","Alex","Herobrine","Creeper","Enderman","Skeleton","Slime","Ghast","Wither","Blaze",
        "Diamond","Emerald","Iron","Coal","Stone","Dirt","Sand","Glass","Wood","Lava",
        "Fast","Swift","Quick","Rapid","Speed","Turbo","Rocket","Jet","Bolt","Dash",
        "Zero","One","Two","Seven","Nine","Ten","First","Last","Prime","Ace",
        "Cat","Dog","Bird","Fish","Frog","Duck","Goat","Pig","Cow","Bee",
        "Rock","Metal","Jazz","Punk","Bass","Drum","Beat","Tune","Song","Vibe",
        "Snow","Hail","Sleet","Chill","Cold","Heat","Warm","Cool","Hot","Burn",
        "North","South","East","West","Up","Down","Left","Right","Top","Core",
        "Wolfie","Kitty","Puppy","Bunny","Teddy","Buddy","Pal","Mate","Bro","Dude"
    };

    static Random rng = new Random(Guid.NewGuid().GetHashCode());
    static Regex nameRegex = new Regex("\"name\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);
    static object consoleLock = new object();
    static Queue<Result> results = new Queue<Result>();
    static Queue<string> ogQueue = new Queue<string>();
    static int mode = 3;
    static bool burst = true;
    static int requestIntervalMs = StableIntervalMs;
    static int floorIntervalMs = StableIntervalMs;
    static int okStreak = 0;
    static object paceLock = new object();
    static long nextSlotMs = 0;
    static long pausedUntilMs = 0;
    static Stopwatch clock = Stopwatch.StartNew();
    static Queue<long> reqTimes = new Queue<long>();
    static Queue<long> printTimes = new Queue<long>();
    static long lastTitleMs = 0;

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

    static double Rate(Queue<long> q, int windowMs)
    {
        long now = clock.ElapsedMilliseconds;
        lock (q)
        {
            while (q.Count > 0 && now - q.Peek() > windowMs) q.Dequeue();
            return q.Count * 1000.0 / windowMs;
        }
    }

    static void Mark(Queue<long> q)
    {
        lock (q) q.Enqueue(clock.ElapsedMilliseconds);
    }

    static string Title()
    {
        return "dzgsniperv3 - made by dzg | " + (burst ? "auto" : "stable") + " " + (1000 * BatchSize / requestIntervalMs) + "/s";
    }

    static void NameLine(ConsoleColor color, string text)
    {
        Mark(printTimes);
        string stats = "rps " + Rate(reqTimes, 5000).ToString("0.0") + " | " + Rate(printTimes, 5000).ToString("0") + " names/s ";
        int width;
        try { width = Console.WindowWidth; } catch { width = 0; }
        if (width < 40) width = 80;
        int pad = width - text.Length - stats.Length - 1;
        if (pad < 2) pad = 2;
        lock (consoleLock)
        {
            Console.ForegroundColor = color;
            Console.Write(text);
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(new string(' ', pad) + stats);
            Console.ResetColor();
            Console.WriteLine();
        }
        long now = clock.ElapsedMilliseconds;
        if (now - lastTitleMs > 500)
        {
            lastTitleMs = now;
            try { Console.Title = Title() + " | " + stats.Trim(); } catch { }
        }
    }

    static HttpWebRequest NewRequest(string url, string method)
    {
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
        req.Method = method;
        req.Proxy = null;
        req.Accept = "application/json";
        req.UserAgent = "dzgsniperv3";
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

    static string RandomName(int len)
    {
        StringBuilder sb = new StringBuilder(len);
        for (int i = 0; i < len; i++)
            sb.Append(Chars[rng.Next(Chars.Length)]);
        return sb.ToString();
    }

    static string Pick(string[] arr)
    {
        return arr[rng.Next(arr.Length)];
    }

    static string NextOg()
    {
        lock (ogQueue)
        {
            if (ogQueue.Count == 0)
            {
                List<string> shuffled = new List<string>(OgWords);
                for (int i = shuffled.Count - 1; i > 0; i--)
                {
                    int j = rng.Next(i + 1);
                    string t = shuffled[i]; shuffled[i] = shuffled[j]; shuffled[j] = t;
                }
                foreach (string w in shuffled) ogQueue.Enqueue(w);
            }
            return ogQueue.Dequeue();
        }
    }

    static string SemiOg()
    {
        string w = Pick(OgWords);
        string name;
        switch (rng.Next(8))
        {
            case 0: name = w + rng.Next(10); break;
            case 1: name = w + rng.Next(10, 100); break;
            case 2: name = w + "_"; break;
            case 3: name = "_" + w; break;
            case 4: name = w + "_" + rng.Next(10); break;
            case 5: name = w + Pick(OgWords); break;
            case 6: name = "x" + w; break;
            default: name = w + "x"; break;
        }
        if (name.Length > 16) name = name.Substring(0, 16);
        return name;
    }

    static string NextName()
    {
        lock (rng)
        {
            switch (mode)
            {
                case 1: return RandomName(3);
                case 2: return RandomName(4);
                case 3: return RandomName(rng.Next(2) == 0 ? 3 : 4);
                case 4: return NextOg();
                default: return SemiOg();
            }
        }
    }

    static List<string> MakeBatch()
    {
        List<string> batch = new List<string>(BatchSize);
        HashSet<string> seen = new HashSet<string>();
        while (batch.Count < BatchSize)
        {
            string name = NextName();
            if (seen.Add(name.ToLowerInvariant())) batch.Add(name);
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

    static void Enqueue(string name, bool taken)
    {
        Result r;
        r.Name = name;
        r.Taken = taken;
        lock (results) results.Enqueue(r);
    }

    static void WaitForSlot()
    {
        while (true)
        {
            long now = clock.ElapsedMilliseconds;
            long wait;
            lock (paceLock)
            {
                long start = Math.Max(now, Math.Max(nextSlotMs, pausedUntilMs));
                wait = start - now;
                if (wait <= 0)
                {
                    nextSlotMs = now + requestIntervalMs;
                    return;
                }
            }
            Thread.Sleep((int)Math.Min(wait, 250));
        }
    }

    static void OnSuccess()
    {
        Mark(reqTimes);
        if (!burst) return;
        lock (paceLock)
        {
            okStreak++;
            if (okStreak >= 8 && requestIntervalMs > floorIntervalMs)
            {
                requestIntervalMs = Math.Max(floorIntervalMs, requestIntervalMs - 50);
                okStreak = 0;
            }
        }
    }

    static void OnRateLimit()
    {
        bool announce = false;
        lock (paceLock)
        {
            okStreak = 0;
            long now = clock.ElapsedMilliseconds;
            if (now >= pausedUntilMs)
            {
                if (burst)
                {
                    floorIntervalMs = Math.Min(MaxRequestIntervalMs, Math.Max(floorIntervalMs, requestIntervalMs * 5 / 4));
                    requestIntervalMs = Math.Min(MaxRequestIntervalMs, Math.Max(floorIntervalMs, requestIntervalMs * 3 / 2));
                }
                pausedUntilMs = now + RateLimitWaitSec * 1000;
                announce = true;
            }
        }
        if (announce)
            Line(ConsoleColor.Yellow, " [!] rate limited by Mojang - retrying in " + RateLimitWaitSec + "s (now " + (1000.0 / requestIntervalMs).ToString("0.0") + " requests/s)");
    }

    static void Fetcher()
    {
        while (true)
        {
            if (Buffered() >= BufferTarget)
            {
                Thread.Sleep(20);
                continue;
            }

            List<string> batch = MakeBatch();
            while (true)
            {
                WaitForSlot();
                HashSet<string> taken = new HashSet<string>();
                int code = CheckBatch(batch, taken);

                if (code == 200)
                {
                    foreach (string n in batch)
                        Enqueue(n, taken.Contains(n.ToLowerInvariant()));
                    OnSuccess();
                    break;
                }
                else if (code == 429)
                {
                    OnRateLimit();
                }
                else
                {
                    Line(ConsoleColor.DarkYellow, " [?] request failed (" + (code == 0 ? "no response" : "HTTP " + code) + ") - retrying in " + ErrorWaitSec + "s");
                    Thread.Sleep(ErrorWaitSec * 1000);
                }
            }
        }
    }

    static void Banner()
    {
        try { Console.Clear(); } catch { }
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  ____  _____ ____ ____  _   _ ___ ____  _____ ____     __     ______  ");
        Console.WriteLine(" |  _ \\|__  // ___/ ___|| \\ | |_ _|  _ \\| ____|  _ \\    \\ \\   / /___ / ");
        Console.WriteLine(" | | | | / /| |  _\\___ \\|  \\| || || |_) |  _| | |_) |    \\ \\ / /  |_ \\ ");
        Console.WriteLine(" | |_| |/ /_| |_| |___) | |\\  || ||  __/| |___|  _ <      \\ V /  ___) |");
        Console.WriteLine(" |____//____|\\____|____/|_| \\_|___|_|   |_____|_| \\_\\      \\_/  |____/ ");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("                 made by dzg  |  Mojang checker  |  v3");
        Console.ResetColor();
        Console.WriteLine();
    }

    static int Ask(string prompt, int min, int max)
    {
        while (true)
        {
            Console.Write(prompt);
            string input = (Console.ReadLine() ?? "").Trim();
            int v;
            if (int.TryParse(input, out v) && v >= min && v <= max) return v;
            Line(ConsoleColor.Red, " pick " + min + "-" + max);
        }
    }

    static void Main()
    {
        Console.Title = "dzgsniperv3 - made by dzg";
        DisableQuickEdit();
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        ServicePointManager.Expect100Continue = false;
        ServicePointManager.DefaultConnectionLimit = Workers + 2;

        Banner();
        Console.WriteLine(" [1] 3 letter names");
        Console.WriteLine(" [2] 4 letter names");
        Console.WriteLine(" [3] both");
        Console.WriteLine(" [4] OG usernames");
        Console.WriteLine(" [5] semi OG usernames");
        Console.WriteLine();
        mode = Ask(" dzgsniperv3> ", 1, 5);

        Console.WriteLine();
        Console.WriteLine(" [1] burst   - peaks at ~100 names/s, backs off if Mojang pushes back");
        Console.WriteLine(" [2] stable  - holds 20 names/s");
        Console.WriteLine(" [3] custom  - hold a rate you choose (above ~20 Mojang will 429)");
        Console.WriteLine(" [4] auto    - starts at 20 and settles at the highest rate Mojang allows");
        Console.WriteLine();
        int speed = Ask(" speed> ", 1, 4);
        burst = speed == 1 || speed == 4;
        int stableNamesPerSec = 20;
        if (speed == 3)
        {
            stableNamesPerSec = Ask(" names per second (10-1000)> ", 10, 1000);
            if (stableNamesPerSec > 20)
                Line(ConsoleColor.Yellow, " above ~20 names/s Mojang will answer 429 and the checker pauses 10s each time");
        }

        if (speed == 1) requestIntervalMs = BurstIntervalMs;
        else if (speed == 4) requestIntervalMs = StableIntervalMs;
        else requestIntervalMs = Math.Max(10, 1000 * BatchSize / stableNamesPerSec);
        floorIntervalMs = speed == 4 ? BurstIntervalMs : requestIntervalMs;

        string modeText;
        if (speed == 1) modeText = " burst mode... press Ctrl+C to stop";
        else if (speed == 4) modeText = " auto mode (starting at 20 names/s)... press Ctrl+C to stop";
        else modeText = " stable mode (" + stableNamesPerSec + " names/s)... press Ctrl+C to stop";

        Console.WriteLine();
        Line(ConsoleColor.DarkGray, " [+] available  [-] taken");
        Line(ConsoleColor.DarkGray, modeText);
        Console.WriteLine();

        for (int i = 0; i < Workers; i++)
        {
            Thread fetcher = new Thread(Fetcher);
            fetcher.IsBackground = true;
            fetcher.Start();
        }

        long next = 0;
        while (true)
        {
            Result r;
            bool have = false;
            int queued;
            lock (results)
            {
                if (results.Count > 0) { r = results.Dequeue(); have = true; }
                else r = new Result();
                queued = results.Count;
            }

            if (have)
            {
                if (r.Taken) NameLine(ConsoleColor.Red, " [-]" + r.Name);
                else NameLine(ConsoleColor.Green, " [+]" + r.Name);
                int printIntervalMs = Math.Max(2, requestIntervalMs / BatchSize);
                if (queued > BufferTarget / 2) printIntervalMs /= 2;
                next += printIntervalMs;
                long wait = next - clock.ElapsedMilliseconds;
                if (wait > 0) Thread.Sleep((int)wait);
                else if (wait < -1000) next = clock.ElapsedMilliseconds;
            }
            else
            {
                Thread.Sleep(5);
                next = clock.ElapsedMilliseconds;
            }
        }
    }
}
