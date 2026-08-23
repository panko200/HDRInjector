using System;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Collections.Generic;
using HarmonyLib;
using SharpGen.Runtime;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using YukkuriMovieMaker.Commons;

namespace HDRInjector.Patches;

/// <summary>
/// プレビュー SwapChain の HDR カラースペース設定と実体を診断するパッチ。
/// [HDRInjector] プレフィックス付きの Debug.WriteLine を出力する。
/// </summary>
public static class HdrPreviewPatch
{
    private static readonly Type? _targetResourcesType =
        AccessTools.TypeByName("YukkuriMovieMaker.Player.TimelineVideoPlayerRenderTargetResources, YukkuriMovieMaker");

    public static void Apply(Harmony harmony)
    {
        try
        {
            Debug.WriteLine($"[HDRInjector][HdrPreviewPatch] Apply start. targetType={_targetResourcesType?.FullName ?? "null"}");
            if (_targetResourcesType == null) return;

            var ctor = _targetResourcesType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(IGraphicsDevicesAndContext), typeof(IntPtr), typeof(int), typeof(int) },
                null
            );

            if (ctor != null)
            {
                var postfix = typeof(HdrPreviewPatch).GetMethod(nameof(CtorPostfix), BindingFlags.Static | BindingFlags.NonPublic);
                var transpiler = typeof(HdrPreviewPatch).GetMethod(nameof(HdrSwapChainConstructorTranspiler), BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(ctor, transpiler: new HarmonyMethod(transpiler), postfix: new HarmonyMethod(postfix));
                Debug.WriteLine($"[HDRInjector][HdrPreviewPatch] Constructor patched for HDR10 format: {ctor}");

                var resize = _targetResourcesType.GetMethod("Resize", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (resize != null)
                {
                    harmony.Patch(resize, transpiler: new HarmonyMethod(transpiler));
                    Debug.WriteLine($"[HDRInjector][HdrPreviewPatch] Resize patched for HDR10 format: {resize}");
                }
            }
            else
            {
                Debug.WriteLine("[HDRInjector][HdrPreviewPatch] Constructor not found.");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HDRInjector][HdrPreviewPatch] Apply exception: {ex}");
        }
    }



    /// <summary>
    /// YMM4本来の B8G8R8A8_UNorm (DXGI_FORMAT=87) を、HDR10用
    /// R16G16B16A16_Float (DXGI_FORMAT=10) に差し替える。
    /// コンストラクタとResizeBuffersの両方に適用する。
    /// </summary>
    private static IEnumerable<CodeInstruction> HdrSwapChainConstructorTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        int replaced = 0;
        foreach (var instruction in instructions)
        {
            if (TryGetInt32Constant(instruction, out var value) && value == 87)
            {
                instruction.opcode = OpCodes.Ldc_I4_S;
                instruction.operand = (sbyte)10;
                replaced++;
            }
            yield return instruction;
        }

        Debug.WriteLine($"[HDRInjector][HdrPreviewPatch] HDR-compatible FP16 format transpiler replaced {replaced} DXGI_FORMAT=87 constants with 10.");
    }

    private static bool TryGetInt32Constant(CodeInstruction instruction, out int value)
    {
        value = 0;
        if (instruction.opcode == OpCodes.Ldc_I4_M1) { value = -1; return true; }
        if (instruction.opcode == OpCodes.Ldc_I4_0) { value = 0; return true; }
        if (instruction.opcode == OpCodes.Ldc_I4_1) { value = 1; return true; }
        if (instruction.opcode == OpCodes.Ldc_I4_2) { value = 2; return true; }
        if (instruction.opcode == OpCodes.Ldc_I4_3) { value = 3; return true; }
        if (instruction.opcode == OpCodes.Ldc_I4_4) { value = 4; return true; }
        if (instruction.opcode == OpCodes.Ldc_I4_5) { value = 5; return true; }
        if (instruction.opcode == OpCodes.Ldc_I4_6) { value = 6; return true; }
        if (instruction.opcode == OpCodes.Ldc_I4_7) { value = 7; return true; }
        if (instruction.opcode == OpCodes.Ldc_I4_8) { value = 8; return true; }
        if (instruction.opcode == OpCodes.Ldc_I4_S && instruction.operand is sbyte sb) { value = sb; return true; }
        if (instruction.opcode == OpCodes.Ldc_I4 && instruction.operand is int i) { value = i; return true; }
        return false;
    }

    private static void DumpObjectMembers(string label, object value, int depth)
    {
        if (value == null)
        {
            Debug.WriteLine($"[HDRInjector][HdrPreviewPatch] {label}=null");
            return;
        }

        var type = value.GetType();
        Debug.WriteLine($"[HDRInjector][HdrPreviewPatch] {label} type={type.FullName}");
        if (depth <= 0) return;

        foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (prop.GetIndexParameters().Length != 0) continue;
            try
            {
                var v = prop.GetValue(value);
                Debug.WriteLine($"[HDRInjector][HdrPreviewPatch] {label}.{prop.Name}={v ?? "null"}");
                if (v != null && v.GetType().IsValueType && !v.GetType().IsPrimitive && v.GetType() != typeof(decimal) && depth > 1)
                    DumpObjectMembers(label + "." + prop.Name, v, depth - 1);
            }
            catch
            {
                Debug.WriteLine($"[HDRInjector][HdrPreviewPatch] {label}.{prop.Name}=<read-error>");
            }
        }

        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            try
            {
                var v = field.GetValue(value);
                Debug.WriteLine($"[HDRInjector][HdrPreviewPatch] {label}.{field.Name}={v ?? "null"}");
            }
            catch { }
        }
    }

    private static void CtorPostfix(object __instance)
    {
        try
        {
            var swapChainProp = _targetResourcesType?.GetProperty(
                "SwapChain", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (swapChainProp?.GetValue(__instance) is not IDXGISwapChain1 swapChain)
            {
                Debug.WriteLine("[HDRInjector][HdrPreviewPatch] SwapChain property was null or not IDXGISwapChain1.");
                return;
            }

            using var sc3 = swapChain.QueryInterfaceOrNull<IDXGISwapChain3>();
            if (sc3 == null)
            {
                Debug.WriteLine("[HDRInjector][HdrPreviewPatch] IDXGISwapChain3 query failed.");
                return;
            }

            try
            {
                DumpObjectMembers("SwapChain.Description", swapChain.Description, 2);

                // Vortice の版によって GetDesc1 / GetDescription1 の公開形が異なるため、
                // 実際に存在するメソッドを reflection で探して呼び出します。
                foreach (var method in swapChain.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (method.Name is not ("GetDesc1" or "GetDescription1")) continue;
                    if (method.GetParameters().Length != 0) continue;
                    try
                    {
                        var result = method.Invoke(swapChain, null);
                        Debug.WriteLine($"[HDRInjector][HdrPreviewPatch] {method.Name}() returned {result?.GetType().FullName ?? "null"}");
                        if (result != null) DumpObjectMembers(method.Name + " result", result, 2);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[HDRInjector][HdrPreviewPatch] {method.Name}() failed: {ex.GetBaseException().Message}");
                    }
                }

                // GetBuffer の公開形も記録しておきます。実際のバックバッファ Format を取得する次段階の手掛かりです。
                foreach (var method in swapChain.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (method.Name == "GetBuffer")
                    {
                        Debug.WriteLine($"[HDRInjector][HdrPreviewPatch] GetBuffer overload: {method}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HDRInjector][HdrPreviewPatch] Failed to inspect SwapChain description: {ex}");
            }

            // DXGI color-space support diagnostics.
            // IMPORTANT: support is checked for the actual swap-chain format.
            // With the FP16 swap-chain we now want the general-purpose Advanced Color path:
            // R16G16B16A16_FLOAT + scRGB (linear BT.709/sRGB primaries).
            foreach (var cs in new[] { (ColorSpaceType)0, (ColorSpaceType)1, (ColorSpaceType)12 })
            {
                try
                {
                    var support = sc3.CheckColorSpaceSupport(cs);
                    Debug.WriteLine($"[HDRInjector][HdrPreviewPatch] CheckColorSpaceSupport FP16 cs={cs}: {support}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[HDRInjector][HdrPreviewPatch] CheckColorSpaceSupport FP16 cs={cs} failed: {ex.Message}");
                }
            }

            try
            {
                var scRgb = (ColorSpaceType)1;
                var support = sc3.CheckColorSpaceSupport(scRgb);
                if (support.HasFlag(SwapChainColorSpaceSupportFlags.Present))
                {
                    sc3.SetColorSpace1(scRgb);
                    Debug.WriteLine($"[HDRInjector][HdrPreviewPatch] SetColorSpace1 SUCCESS: scRGB={scRgb}, support={support}");
                }
                else
                {
                    Debug.WriteLine($"[HDRInjector][HdrPreviewPatch] SetColorSpace1 FAILED/UNSUPPORTED: scRGB={scRgb}, support={support}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HDRInjector][HdrPreviewPatch] SetColorSpace1 scRGB exception: {ex}");
            }

            try
            {
                // IDXGISwapChain3 の ColorSpace1 プロパティも Vortice の版によって公開名が異なるため reflection で取得。
                var prop = sc3.GetType().GetProperty("ColorSpace1", BindingFlags.Instance | BindingFlags.Public);
                if (prop != null)
                {
                    var current = prop.GetValue(sc3);
                    Debug.WriteLine($"[HDRInjector][HdrPreviewPatch] Current ColorSpace1={current}");
                }
                else
                {
                    Debug.WriteLine("[HDRInjector][HdrPreviewPatch] Current ColorSpace1=<unavailable via public property>");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HDRInjector][HdrPreviewPatch] Reading Current ColorSpace1 failed: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HDRInjector][HdrPreviewPatch] CtorPostfix exception: {ex}");
        }
    }
}
