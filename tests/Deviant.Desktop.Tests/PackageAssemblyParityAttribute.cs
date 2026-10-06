using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit.Sdk;

[assembly:Deviant.Desktop.Tests.PackageAssemblyParity]

namespace Deviant.Desktop.Tests;

[AttributeUsage(AttributeTargets.Assembly|AttributeTargets.Class|AttributeTargets.Method)]
public sealed class PackageAssemblyParityAttribute:BeforeAfterTestAttribute
{
    public override void After(MethodInfo methodUnderTest)
    {
        var package=Environment.GetEnvironmentVariable("DEVIANT_QA_PACKAGE");if(string.IsNullOrEmpty(package))return;
        using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(package,"package-manifest.json")));
        var expected=manifest.RootElement.GetProperty("files").EnumerateArray().ToDictionary(entry=>entry.GetProperty("file").GetString()!,entry=>entry.GetProperty("sha256").GetString()!,StringComparer.OrdinalIgnoreCase);
        var loaded=AppDomain.CurrentDomain.GetAssemblies().Where(assembly=>assembly.GetName().Name!.StartsWith("Deviant.",StringComparison.Ordinal)&&assembly.GetName().Name!="Deviant.Desktop.Tests").Select(assembly=>new{File=Path.GetFileName(assembly.Location),Sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant()}).ToArray();
        foreach(var assembly in loaded)if(!expected.TryGetValue(assembly.File,out var hash)||hash!=assembly.Sha256)throw new InvalidOperationException("Loaded assembly does not match package: "+assembly.File);
        var evidence=Environment.GetEnvironmentVariable("DEVIANT_QA_EVIDENCE");if(string.IsNullOrEmpty(evidence))return;
        Directory.CreateDirectory(evidence);File.WriteAllText(Path.Combine(evidence,"package-test-assemblies-"+Guid.NewGuid().ToString("N")+".json"),JsonSerializer.Serialize(new{Test=methodUnderTest.Name,Assemblies=loaded},new JsonSerializerOptions{WriteIndented=true}));
    }
}
