using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace CodexTraffic;

// Decides whether an executable may be started with administrator rights at
// logon without a UAC prompt. That is only safe when no ordinary process of the
// user can replace the executable, plant a DLL beside it, or rename one of its
// folders. Installing to Program Files (the installer default) satisfies this;
// %LOCALAPPDATA%, the desktop, Downloads or a portable folder do not.
internal static class InstallLocationSecurity
{
    internal readonly record struct AccessEntry(string Sid, int Mask, bool Allow, bool InheritOnly);

    private const int GenericAll = 0x10000000;
    private const int GenericWrite = 0x40000000;

    internal const FileSystemRights FileWrite =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    // Creating files allows DLL planting; creating folders could add a missing
    // amd64 folder; deleting children allows replacing the executable.
    internal const FileSystemRights DirectoryWrite =
        FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    // Higher folders only matter if they let a child folder be removed or renamed.
    internal const FileSystemRights AncestorWrite =
        FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private static readonly HashSet<string> Trusted = new(StringComparer.OrdinalIgnoreCase)
    {
        "S-1-5-18",     // SYSTEM
        "S-1-5-32-544", // Administrators
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", // TrustedInstaller
    };

    internal static bool GrantsUntrustedWrite(string? ownerSid, IEnumerable<AccessEntry> entries, FileSystemRights dangerous)
    {
        if (ownerSid == null || !Trusted.Contains(ownerSid)) return true; // An owner can always rewrite the ACL.
        foreach (var entry in entries)
        {
            if (!entry.Allow || entry.InheritOnly || Trusted.Contains(entry.Sid)) continue;
            if ((entry.Mask & (GenericAll | GenericWrite)) != 0 || (entry.Mask & (int)dangerous) != 0) return true;
        }
        return false;
    }

    internal static bool IsProtected(string? executable)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable) || executable.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            var file = new FileInfo(Path.GetFullPath(executable));
            if (!file.Exists || file.Directory == null) return false;
            if (!Check(file, FileWrite)) return false;
            var directory = file.Directory;
            if (!Check(directory, DirectoryWrite)) return false;
            foreach (var library in directory.EnumerateFiles("*.dll"))
                if (!Check(library, FileWrite)) return false;
            var natives = new DirectoryInfo(Path.Combine(directory.FullName, "amd64"));
            if (natives.Exists)
            {
                if (!Check(natives, DirectoryWrite)) return false;
                foreach (var library in natives.EnumerateFiles("*.dll"))
                    if (!Check(library, FileWrite)) return false;
            }
            for (var ancestor = directory.Parent; ancestor != null; ancestor = ancestor.Parent)
                if (!Check(ancestor, AncestorWrite)) return false;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SystemException)
        {
            return false;
        }
    }

    private static bool Check(FileSystemInfo item, FileSystemRights dangerous)
    {
        if ((item.Attributes & FileAttributes.ReparsePoint) != 0) return false; // A junction could point anywhere.
        FileSystemSecurity security = item is DirectoryInfo directory ? directory.GetAccessControl() : ((FileInfo)item).GetAccessControl();
        string? owner = (security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value;
        var entries = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(rule => new AccessEntry(((SecurityIdentifier)rule.IdentityReference).Value, (int)rule.FileSystemRights,
                rule.AccessControlType == AccessControlType.Allow, (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0));
        return !GrantsUntrustedWrite(owner, entries, dangerous);
    }
}
