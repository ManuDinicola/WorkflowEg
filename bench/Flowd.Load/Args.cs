namespace Flowd.Load;

internal sealed record Args(string[] Urls, int Instances, int Starters, int Workers)
{
    public static Args Parse(string[] args)
    {
        string Get(string name, string fallback)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        }

        return new Args(Get("--urls", "http://127.0.0.1:8080").Split(',', StringSplitOptions.RemoveEmptyEntries), int.Parse(Get("--instances", "1000")), int.Parse(Get("--starters", "16")), int.Parse(Get("--workers", "8")));
    }
}
