using System.IO;
using System.Security.AccessControl;

namespace CodexTraffic;

// The logon task runs the monitor with administrator rights without a UAC
// prompt, so its executable (and the native DLLs beside it) must live where an
// ordinary process of the same user cannot replace them.
internal static class InstallLocationSelfTests
{
    private const string User = "S-1-5-21-1000-2000-3000-1001";
    private const string Users = "S-1-5-32-545";
    private const string AuthenticatedUsers = "S-1-5-11";
    private const string CreatorOwner = "S-1-3-0";
    private const string Administrators = "S-1-5-32-544";
    private const string System = "S-1-5-18";
    private const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    internal static IReadOnlyList<string> Run()
    {
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAILED: " + name);
            checks.Add(name);
        }
        static InstallLocationSecurity.AccessEntry Allow(string sid, FileSystemRights rights, bool inheritOnly = false) => new(sid, (int)rights, true, inheritOnly);

        var programFiles = new[]
        {
            Allow(TrustedInstaller, FileSystemRights.FullControl),
            Allow(System, FileSystemRights.Modify),
            Allow(Administrators, FileSystemRights.Modify),
            Allow(Users, FileSystemRights.ReadAndExecute),
            Allow(CreatorOwner, (FileSystemRights)0x10000000, inheritOnly: true), // GENERIC_ALL for new children only
        };
        Check(!InstallLocationSecurity.GrantsUntrustedWrite(TrustedInstaller, programFiles, InstallLocationSecurity.DirectoryWrite),
            "A Program Files style directory (admins write, users read, inherit-only CREATOR OWNER) is protected");

        var userProfile = new[] { Allow(System, FileSystemRights.FullControl), Allow(Administrators, FileSystemRights.FullControl), Allow(User, FileSystemRights.FullControl) };
        Check(InstallLocationSecurity.GrantsUntrustedWrite(User, userProfile, InstallLocationSecurity.DirectoryWrite),
            "A per-user directory such as %LOCALAPPDATA%\\Programs is not protected");
        Check(InstallLocationSecurity.GrantsUntrustedWrite(User, programFiles, InstallLocationSecurity.DirectoryWrite),
            "A directory owned by the ordinary user is not protected, because the owner can rewrite its permissions");
        Check(InstallLocationSecurity.GrantsUntrustedWrite(TrustedInstaller, programFiles.Append(Allow(Users, (FileSystemRights)0x40000000)), InstallLocationSecurity.DirectoryWrite),
            "A GENERIC_WRITE grant to Users is treated as write access");
        Check(InstallLocationSecurity.GrantsUntrustedWrite(TrustedInstaller, programFiles.Append(Allow(Users, FileSystemRights.CreateFiles)), InstallLocationSecurity.DirectoryWrite),
            "Letting users create files beside the executable allows DLL planting");
        Check(!InstallLocationSecurity.GrantsUntrustedWrite(TrustedInstaller, programFiles.Append(new InstallLocationSecurity.AccessEntry(Users, (int)FileSystemRights.FullControl, false, false)), InstallLocationSecurity.DirectoryWrite),
            "Deny entries never count as write access");

        var driveRoot = new[]
        {
            Allow(TrustedInstaller, FileSystemRights.FullControl), Allow(Administrators, FileSystemRights.FullControl), Allow(System, FileSystemRights.FullControl),
            Allow(Users, FileSystemRights.ReadAndExecute),
            Allow(AuthenticatedUsers, FileSystemRights.AppendData),
            Allow(AuthenticatedUsers, FileSystemRights.Modify, inheritOnly: true),
        };
        Check(!InstallLocationSecurity.GrantsUntrustedWrite(TrustedInstaller, driveRoot, InstallLocationSecurity.AncestorWrite),
            "A system drive root that only lets users create new folders cannot redirect an existing install directory");
        Check(InstallLocationSecurity.GrantsUntrustedWrite(TrustedInstaller, driveRoot, InstallLocationSecurity.DirectoryWrite),
            "The same drive root is not a safe place for the executable itself");
        Check(InstallLocationSecurity.GrantsUntrustedWrite(TrustedInstaller, driveRoot.Append(Allow(Users, FileSystemRights.DeleteSubdirectoriesAndFiles)), InstallLocationSecurity.AncestorWrite),
            "An ancestor that lets users delete or rename children is not protected");

        string directory = Path.Combine(Path.GetTempPath(), "CodexInstallLocation", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            string executable = Path.Combine(directory, "CodexTrafficMonitor.exe");
            File.WriteAllBytes(executable, Array.Empty<byte>());
            Check(!InstallLocationSecurity.IsProtected(executable), "An executable in the user's temporary directory is never trusted for elevated autostart");
            Check(!InstallLocationSecurity.IsProtected(Path.Combine(directory, "missing.exe")) && !InstallLocationSecurity.IsProtected("CodexTrafficMonitor.exe")
                  && !InstallLocationSecurity.IsProtected(@"\\server\share\CodexTrafficMonitor.exe"),
                "Missing, relative and network executables are never trusted");
            var refused = StartupManager.ProtectedLocationError(executable);
            Check(refused.Contains("Program Files") && refused.Contains(executable), "Refusing an unprotected location names the path and the safe install location");
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
        return checks;
    }
}
