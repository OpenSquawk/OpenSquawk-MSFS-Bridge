#nullable enable
using System;
using System.IO;
using System.Reflection;
using OpensquawkBridge.Abstractions;

internal static class SimAdapterLoader
{
    private const string AdapterAssemblyName = "OpensquawkBridge.SimConnectAdapter.dll";
    private const string AdapterTypeName = "OpensquawkBridge.SimConnectAdapter.SimConnectAdapter";

    public static bool TryLoad(out SimAdapterHandle? handle, out Exception? error)
    {
        var assemblyPath = Path.Combine(AppContext.BaseDirectory, AdapterAssemblyName);
        if (!File.Exists(assemblyPath))
        {
            handle = null;
            error = new FileNotFoundException("SimConnect adapter assembly not found.", assemblyPath);
            return false;
        }

        try
        {
            var assembly = Assembly.LoadFrom(assemblyPath);
            var type = assembly.GetType(AdapterTypeName, throwOnError: true)!;
            if (Activator.CreateInstance(type) is not ISimConnectAdapter adapter)
            {
                handle = null;
                error = new InvalidOperationException($"Type '{AdapterTypeName}' does not implement ISimConnectAdapter.");
                return false;
            }

            handle = new SimAdapterHandle(adapter, assembly);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            handle = null;
            error = ex;
            return false;
        }
    }

    public static bool IsSimConnectLoadFailure(Exception ex)
    {
        if (ex is BadImageFormatException || ex is DllNotFoundException)
        {
            return true;
        }

        if (ex is FileLoadException fileLoad && fileLoad.HResult == unchecked((int)0x8007000B))
        {
            return true;
        }

        return ex.InnerException != null && IsSimConnectLoadFailure(ex.InnerException);
    }
}

internal sealed class SimAdapterHandle : IDisposable
{
    public SimAdapterHandle(ISimConnectAdapter adapter, Assembly assembly)
    {
        Adapter = adapter;
        Assembly = assembly;
    }

    public ISimConnectAdapter Adapter { get; }
    public Assembly Assembly { get; }

    public void Dispose()
    {
        Adapter.Dispose();
    }
}
