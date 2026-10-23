using SkyrimVersionSwitcher.Core;

internal static class FeedbackTests
{
    public static void Run()
    {
        void Assert(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
        Assert(ExecutableVersion.Resolve("1.5.97.0", 1, 0, 0) == "1.5.97", "Skyrim string version overrides stale fixed numeric version");
        Assert(ExecutableVersion.Resolve("1.6.1170.0", 1, 0, 0) == "1.6.1170" && ExecutableVersion.Resolve("1.7.104.0", 1, 0, 0) == "1.7.104", "modern runtime version resources are normalized");
        Assert(ExecutableVersion.Resolve(null, 1, 5, 97) == "1.5.97", "missing string version falls back to fixed metadata");
        try { ExecutableVersion.Resolve("invalid", 1, 5, 97); throw new Exception("Invalid metadata accepted"); }
        catch (IOException) { Assert(true, "malformed version cannot fall back to misleading fixed metadata"); }
        try { ExecutableVersion.Require(typeof(FeedbackTests).Assembly.Location, "9.9.9"); throw new Exception("Wrong runtime accepted"); }
        catch (IOException error) { Assert(error.Message.Contains("expected Skyrim 9.9.9, found") && error.Message.Contains(typeof(FeedbackTests).Assembly.Location), "wrong executable is rejected with expected version, actual version and path"); }
        Assert(new WorkProgress("Verify", "file", 4, 8).Percent == 50 && new WorkProgress("Verify", "file", 4, 8).Counter == "4 / 8 files", "verification progress exposes actual completed-file counts");
        Assert(new WorkProgress("Steam", "Signing in").Percent == null && new WorkProgress("Steam", "Signing in", 0, 0).Percent == null, "unknown download totals never produce a fabricated percentage");
        Assert(new WorkProgress("Tool", "Downloading", 1048576, 2097152, "bytes").Counter == "1.0 MB / 2.0 MB", "HTTP transfer progress formats real byte counts");
        var access = ErrorMessages.Describe(new AcquisitionException(AcquisitionFailure.SteamAccessOrTransport, "fixture"));
        Assert(access.NextStep.Contains("Steam Guard") && access.NextStep.Contains("Retry"), "Steam errors point to authentication console and safe retry");
        var mismatch = ErrorMessages.Describe(new AcquisitionException(AcquisitionFailure.ContentMismatch, "fixture"));
        Assert(mismatch.Title.Contains("verification") && !mismatch.Title.Contains("Steam download"), "verification failure is distinguished from Steam connectivity failure");
        var disk = ErrorMessages.Describe(new IOException("disk", unchecked((int)0x80070070)));
        Assert(disk.Title.Contains("disk space"), "disk-full errors explain space requirements");
        var locked = ErrorMessages.Describe(new IOException("locked", unchecked((int)0x80070020)));
        Assert(locked.Title.Contains("in use") && locked.NextStep.Contains("Close"), "locked-file errors explain which applications to close");
        var recovery = ErrorMessages.Describe(new IOException("write error"), true);
        Assert(recovery.Title.Contains("recovery") && recovery.NextStep.Contains("Recover files"), "pending recovery takes priority over a generic retry");
        Assert(ErrorMessages.Describe(new IOException("Enter your Steam username.")).Title.Contains("username"), "missing Steam account is an actionable form error");
    }
}
