using System;
using System.Runtime.InteropServices;

namespace ShurikenCascade
{
    internal readonly struct CascadeShaderInstructions
    {
        internal readonly uint Float, Int, UInt, Total, Samples, Loads, StaticFlow, DynamicFlow, Registers;
        internal long Alu => (long)Float + Int + UInt;
        internal CascadeShaderInstructions(uint floating, uint signed, uint unsigned, uint total, uint samples, uint loads, uint staticFlow, uint dynamicFlow, uint registers)
        { Float = floating; Int = signed; UInt = unsigned; Total = total; Samples = samples; Loads = loads; StaticFlow = staticFlow; DynamicFlow = dynamicFlow; Registers = registers; }
    }

    internal static class CascadeD3D11Reflection
    {
        // Layout of the public Windows SDK D3D11_SHADER_DESC. Creator is pointer-sized.
        [StructLayout(LayoutKind.Sequential)]
        struct Description
        {
            internal uint Version;
            internal IntPtr Creator;
            internal uint Flags, ConstantBuffers, BoundResources, InputParameters, OutputParameters, InstructionCount,
                TempRegisterCount, TempArrayCount, DefCount, DclCount, TextureNormalInstructions, TextureLoadInstructions,
                TextureCompInstructions, TextureBiasInstructions, TextureGradientInstructions, FloatInstructionCount,
                IntInstructionCount, UintInstructionCount, StaticFlowControlCount, DynamicFlowControlCount,
                MacroInstructionCount, ArrayInstructionCount, CutInstructionCount, EmitInstructionCount, GSOutputTopology,
                GSMaxOutputVertexCount, InputPrimitive, PatchConstantParameters, GSInstanceCount, ControlPoints,
                HSOutputPrimitive, HSPartitioning, TessellatorDomain, BarrierInstructions, InterlockedInstructions, TextureStoreInstructions;
        }
        [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
        static extern int D3DReflect(byte[] data, UIntPtr size, ref Guid iid, out IntPtr reflection);
        [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
        static extern int D3DDisassemble(byte[] data, UIntPtr size, uint flags, IntPtr comments, out IntPtr assembly);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetDescription(IntPtr self, out Description description);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint ReleaseObject(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr GetBufferPointer(IntPtr self);

        internal static bool TryRead(byte[] data, out CascadeShaderInstructions instructions, out string reason)
        {
            instructions = default;
            reason = Validate(data); if (reason != null) return false;
            IntPtr reflection = IntPtr.Zero;
            try
            {
                var iid = new Guid("8d536ca1-0cca-4956-a837-786963755584");
                int result = D3DReflect(data, (UIntPtr)data.Length, ref iid, out reflection);
                if (result < 0 || reflection == IntPtr.Zero) { reason = "DXBC 无可用反射数据，HRESULT 0x" + result.ToString("X8"); return false; }
                var table = Marshal.ReadIntPtr(reflection);
                var get = Marshal.GetDelegateForFunctionPointer<GetDescription>(Marshal.ReadIntPtr(table, 3 * IntPtr.Size));
                result = get(reflection, out var d);
                if (result < 0) { reason = "无法读取 Shader 描述，HRESULT 0x" + result.ToString("X8"); return false; }
                if (d.InstructionCount == 0) return ReadDisassembly(data, out instructions, out reason);
                instructions = new CascadeShaderInstructions(d.FloatInstructionCount, d.IntInstructionCount, d.UintInstructionCount,
                    d.InstructionCount, d.TextureNormalInstructions + d.TextureCompInstructions + d.TextureBiasInstructions + d.TextureGradientInstructions,
                    d.TextureLoadInstructions, d.StaticFlowControlCount, d.DynamicFlowControlCount, d.TempRegisterCount);
                reason = "统计来源：D3DReflect / DXBC STAT"; return true;
            }
            catch (Exception ex) { reason = "D3D11 反射不可用：" + ex.Message; return false; }
            finally
            {
                if (reflection != IntPtr.Zero)
                {
                    var release = Marshal.GetDelegateForFunctionPointer<ReleaseObject>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(reflection), 2 * IntPtr.Size));
                    release(reflection);
                }
            }
        }
        static bool ReadDisassembly(byte[] data, out CascadeShaderInstructions instructions, out string reason)
        {
            instructions = default; IntPtr blob = IntPtr.Zero;
            try
            {
                int code = D3DDisassemble(data, (UIntPtr)data.Length, 0, IntPtr.Zero, out blob);
                if (code < 0 || blob == IntPtr.Zero) { reason = "STAT 缺失且反汇编失败，HRESULT 0x" + code.ToString("X8"); return false; }
                var pointer = Marshal.GetDelegateForFunctionPointer<GetBufferPointer>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(blob), 3 * IntPtr.Size));
                return CascadeDxbcInstructions.TryCount(Marshal.PtrToStringAnsi(pointer(blob)) ?? "", out instructions, out reason);
            }
            finally
            {
                if (blob != IntPtr.Zero) Marshal.GetDelegateForFunctionPointer<ReleaseObject>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(blob), 2 * IntPtr.Size))(blob);
            }
        }
        internal static string Validate(byte[] data)
        {
            if (data == null || data.Length < 32 || BitConverter.ToUInt32(data, 0) != 0x43425844) return "没有 DXBC 字节码";
            uint size = BitConverter.ToUInt32(data, 24), chunks = BitConverter.ToUInt32(data, 28);
            if (size != data.Length || chunks > (data.Length - 32) / 4) return "无效的 DXBC 容器";
            for (int i = 0; i < chunks; i++)
            {
                uint offset = BitConverter.ToUInt32(data, 32 + i * 4);
                if (offset > data.Length - 8) return "无效的 DXBC 块位置";
                uint length = BitConverter.ToUInt32(data, (int)offset + 4);
                if (length > data.Length - offset - 8) return "无效的 DXBC 块长度";
                if (BitConverter.ToUInt32(data, (int)offset) == 0x4C495844) return "DXIL 暂不支持 D3D11 反射统计";
            }
            return null;
        }
    }
}
