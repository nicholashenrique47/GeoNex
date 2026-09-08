using System.Security.Cryptography;
using GeoNex.Services;

internal static class ManagedInteropContracts
{
    public static void Run()
    {
        string expectedPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "GeoNexNative.dll"));
        Assert(NativeMethods.AbiVersion == NativeMethods.ExpectedAbiVersion, "managed ABI check");
        Assert(string.Equals(NativeMethods.LoadedPath, expectedPath, StringComparison.OrdinalIgnoreCase),
            "managed loader must bind the output-local DLL");

        using (FileStream stream = File.OpenRead(expectedPath))
        {
            string actualHash = Convert.ToHexString(SHA256.HashData(stream));
            Assert(NativeMethods.LoadedSha256 == actualHash, "managed loader SHA256");
        }

        nint cancellation = NativeMethods.CreateRenderCancellation();
        Assert(cancellation != 0, "managed cancellation allocation");
        CancellationTokenRegistration registration = default;
        try
        {
            using var source = new CancellationTokenSource();
            registration = source.Token.UnsafeRegister(
                static state => NativeMethods.CancelRender((nint)state!), cancellation);
            source.Cancel();
        }
        finally
        {
            // The registration must finish callbacks before native destruction.
            registration.Dispose();
            NativeMethods.DestroyRenderCancellation(cancellation);
        }

        Console.WriteLine($"Managed interop contracts: PASS (ABI={NativeMethods.AbiVersion}; SHA256={NativeMethods.LoadedSha256}; Path={NativeMethods.LoadedPath})");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
