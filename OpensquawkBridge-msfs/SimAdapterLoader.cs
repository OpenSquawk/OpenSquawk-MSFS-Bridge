#nullable enable
using System;
using System.IO;
using System.Reflection;
using OpensquawkBridge.Abstractions;

internal static class SimAdapterLoader
{
    private const string AdapterAssemblySimpleName = "OpensquawkBridge.SimConnectAdapter";
    private const string AdapterAssemblyFileName = AdapterAssemblySimpleName + ".dll";
    private const string AdapterTypeName = "OpensquawkBridge.SimConnectAdapter.SimConnectAdapter";

    public static bool TryLoad(out SimAdapterHandle? handle, out Exception? error)
    {
        try
        {
            var assembly = TryLoadAdapterAssembly(out var loadError);
            if (assembly == null)
            {
                handle = null;
                error = loadError ?? new FileNotFoundException("SimConnect adapter assembly not found.");
                return false;
            }

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

    private static Assembly? TryLoadAdapterAssembly(out Exception? error)
    {
        error = null;

        try
        {
            // Works for single-file publish where the adapter is bundled.
            return Assembly.Load(AdapterAssemblySimpleName);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        var assemblyPath = Path.Combine(AppContext.BaseDirectory, AdapterAssemblyFileName);
        if (!File.Exists(assemblyPath))
        {
            return null;
        }

        try
        {
            return Assembly.LoadFrom(assemblyPath);
        }
        catch (Exception ex)
        {
            error = ex;
            return null;
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
