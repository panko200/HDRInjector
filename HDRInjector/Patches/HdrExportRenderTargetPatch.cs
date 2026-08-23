using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace HDRInjector.Patches;

/// <summary>
/// YMM4's export path allocates its render target with CreateNotInitializedBitmap(),
/// whose default format is B8G8R8A8_UNorm. The HDR writer needs the same FP16
/// intermediate target used by the preview HDR bridge. This patch swaps that one
/// allocation to R16G16B16A16_Float only while an HDR writer export is active.
/// </summary>
internal static class HdrExportRenderTargetPatch
{
    private static readonly AsyncLocal<bool> HdrExportActive = new();

    public static bool IsActive => HdrExportActive.Value;

    public static void BeginExport() => HdrExportActive.Value = true;

    public static void EndExport() => HdrExportActive.Value = false;

    public static void Apply(Harmony harmony)
    {
        try
        {
            int patched = 0;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch { continue; }

                foreach (var type in types)
                {
                    MethodInfo[] methods;
                    try
                    {
                        methods = type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    }
                    catch { continue; }

                    foreach (var method in methods.Where(m =>
                        m.Name == "CreateNotInitializedBitmap" &&
                        typeof(ID2D1Bitmap1).IsAssignableFrom(m.ReturnType)))
                    {
                        var parameters = method.GetParameters();
                        if (parameters.Length < 3)
                            continue;

                        if (!parameters.Any(p => p.ParameterType == typeof(int)) ||
                            !parameters.Any(p => p.ParameterType == typeof(BitmapOptions)))
                            continue;

                        var first = parameters[0].ParameterType;
                        if (!typeof(ID2D1DeviceContext).IsAssignableFrom(first) &&
                            !typeof(ID2D1DeviceContext6).IsAssignableFrom(first))
                            continue;

                        var prefix = typeof(HdrExportRenderTargetPatch).GetMethod(
                            nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic);

                        harmony.Patch(method, prefix: new HarmonyMethod(prefix));
                        patched++;

                        System.Diagnostics.Debug.WriteLine(
                            $"[HDRInjector][HdrExportTarget] Patched {method.DeclaringType?.FullName}.{method.Name} " +
                            $"({string.Join(", ", parameters.Select(p => p.ParameterType.Name))})");
                    }
                }
            }

            System.Diagnostics.Debug.WriteLine($"[HDRInjector][HdrExportTarget] Apply complete. patched={patched}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HDRInjector][HdrExportTarget] Apply error: {ex}");
        }
    }

    private static bool Prefix(object[] __args, ref ID2D1Bitmap1 __result)
    {
        if (!HdrExportActive.Value)
            return true;

        try
        {
            var deviceContext = __args.FirstOrDefault(x => x is ID2D1DeviceContext) as ID2D1DeviceContext;
            if (deviceContext == null)
                return true;

            var intArgs = __args.OfType<int>().ToArray();
            if (intArgs.Length < 2)
                return true;

            int width = Math.Max(1, intArgs[0]);
            int height = Math.Max(1, intArgs[1]);

            BitmapOptions options = BitmapOptions.Target | BitmapOptions.CannotDraw;
            var optionArg = __args.FirstOrDefault(x => x is BitmapOptions);
            if (optionArg is BitmapOptions requested)
                options = requested;

            var props = new BitmapProperties1(
                new PixelFormat(Format.R16G16B16A16_Float, Vortice.DCommon.AlphaMode.Premultiplied),
                96.0f,
                96.0f,
                options);

            __result = deviceContext.CreateBitmap(
                new SizeI(width, height),
                IntPtr.Zero,
                width * 8,
                props);

            System.Diagnostics.Debug.WriteLine(
                $"[HDRInjector][HdrExportTarget] Created FP16 export target {width}x{height}, " +
                $"format=R16G16B16A16_Float options={options}");

            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HDRInjector][HdrExportTarget] Prefix failed: {ex}");
            return true;
        }
    }
}
