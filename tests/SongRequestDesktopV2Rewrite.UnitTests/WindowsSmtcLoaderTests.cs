using System;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

/// <summary>
/// The Windows SMTC backend is a Windows-only companion assembly loaded at
/// runtime. These tests pin the loader's graceful-degradation behaviour and its
/// contract with the companion's public surface (type + entry point + target).
/// </summary>
public class WindowsSmtcLoaderTests
{
    [Fact]
    public void Available_IsFalse_WhenCompanionIsNotDeployed()
    {
        if (OperatingSystem.IsWindows() && WindowsSmtcLoader.ResolvePath() != null)
            return; // a Windows build may deploy the companion next to the app

        Assert.False(WindowsSmtcLoader.Available);
    }

    [Fact]
    public void ResolvePath_ReturnsNullWhenMissing_AndFindsTheDeployedFile()
    {
        var expected = Path.Combine(AppContext.BaseDirectory, WindowsSmtcLoader.AssemblyFileName);
        if (File.Exists(expected))
        {
            Assert.Equal(expected, WindowsSmtcLoader.ResolvePath());
            return;
        }

        Assert.Null(WindowsSmtcLoader.ResolvePath());

        try
        {
            File.WriteAllBytes(expected, Array.Empty<byte>());
            Assert.Equal(expected, WindowsSmtcLoader.ResolvePath());
        }
        finally
        {
            File.Delete(expected);
        }
    }

    [Fact]
    public void TryCreate_ReturnsNull_WhenUnsupportedOrUnavailable()
    {
        // Not Windows -> never loads; on Windows without the companion -> still null.
        Assert.Null(WindowsSmtcLoader.TryCreate(IntPtr.Zero));
        Assert.Null(WindowsSmtcLoader.TryCreate(new IntPtr(0x1234)));
    }

    [Fact]
    public void CompanionAssembly_ExportsTheTypeAndEntryPointTheLoaderLooksFor()
    {
        var path = FindCompanionAssembly();
        if (path == null) return; // companion not built in this layout

        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();

        var assemblyName = reader.GetString(reader.GetAssemblyDefinition().Name);
        Assert.Equal(Path.GetFileNameWithoutExtension(WindowsSmtcLoader.AssemblyFileName), assemblyName);

        var tfm = ReadStringAttribute(reader, "TargetFrameworkAttribute");
        Assert.NotNull(tfm);
        Assert.StartsWith(".NETCoreApp", tfm!, StringComparison.Ordinal);

        var targetPlatform = ReadStringAttribute(reader, "TargetPlatformAttribute");
        Assert.NotNull(targetPlatform);
        Assert.StartsWith("windows", targetPlatform!, StringComparison.OrdinalIgnoreCase);

        var match = reader.TypeDefinitions
            .Select(reader.GetTypeDefinition)
            .FirstOrDefault(t => TypeName(reader, t) == WindowsSmtcLoader.TypeName);

        Assert.False(match.Name.IsNil, $"{WindowsSmtcLoader.TypeName} not found in {path}");

        var methods = match.GetMethods().Select(m => reader.GetString(reader.GetMethodDefinition(m).Name)).ToList();
        Assert.Contains(WindowsSmtcLoader.EntryPointName, methods);

        // The loader casts the result to ISystemMediaSession.
        var interfaces = match.GetInterfaceImplementations()
            .Select(i => reader.GetInterfaceImplementation(i).Interface)
            .Select(h => h.Kind switch
            {
                HandleKind.TypeReference => reader.GetString(reader.GetTypeReference((TypeReferenceHandle)h).Name),
                HandleKind.TypeDefinition => reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)h).Name),
                _ => string.Empty,
            });
        Assert.Contains("ISystemMediaSession", interfaces);
    }

    private static string TypeName(MetadataReader reader, TypeDefinition type)
        => $"{reader.GetString(type.Namespace)}.{reader.GetString(type.Name)}";

    private static string? ReadStringAttribute(MetadataReader reader, string attributeTypeName)
    {
        foreach (var handle in reader.CustomAttributes)
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;

            var ctor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (ctor.Parent.Kind != HandleKind.TypeReference) continue;

            var type = reader.GetTypeReference((TypeReferenceHandle)ctor.Parent);
            if (reader.GetString(type.Name) != attributeTypeName) continue;

            var blob = reader.GetBlobReader(attribute.Value);
            // Prolog (0x0001) followed by a SerString value.
            if (blob.ReadUInt16() != 0x0001) continue;
            return blob.ReadSerializedString();
        }

        return null;
    }

    private static string? FindCompanionAssembly()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var bin = Path.Combine(dir.FullName, "SongRequestDesktopV2Rewrite.Smtc.Windows", "bin");
            if (Directory.Exists(bin))
            {
                var found = Directory
                    .EnumerateFiles(bin, WindowsSmtcLoader.AssemblyFileName, SearchOption.AllDirectories)
                    .FirstOrDefault(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));
                if (found != null) return found;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
