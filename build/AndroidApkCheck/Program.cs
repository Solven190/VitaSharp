using VitaSharp.AndroidApkCheck;

// build/build-android-apk.ps1 / build/build-android-apk.sh: checks the arm64 Native AOT APK (ApkContents). Usage:
// AndroidApkCheck <apk>; exit code 0 when it passes, 1 with the problems on stderr, 2 for a wrong command line.
if (args.Length != 1)
{
    Console.Error.WriteLine("usage: AndroidApkCheck <apk>");
    return 2;
}

List<string> problems = ApkContents.Problems(args[0]);
if (problems.Count > 0)
{
    Console.Error.WriteLine($"APK check failed: {args[0]}");
    foreach (string problem in problems)
    {
        Console.Error.WriteLine($"  {problem}");
    }

    return 1;
}

Console.WriteLine($"APK check: ok ({ApkContents.Abi}, Native AOT, {string.Join(", ", ApkContents.RequiredLibraries.Skip(1))})");
return 0;
