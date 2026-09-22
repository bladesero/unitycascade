using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ShurikenCascade
{
    // A conservative static IR fallback for Unity's external-tool DXBC, which strips the STAT chunk.
    // Counts vector instructions once, not scalar lanes, loop executions, FLOPs or hardware cycles.
    internal static class CascadeDxbcInstructions
    {
        static HashSet<string> Set(string value) => new HashSet<string>(value.Split(' '));
        static readonly HashSet<string> floating = Set("add div dp2 dp3 dp4 exp frc log mad min max mul round_ne round_ni round_pi round_z rsq sqrt sincos rcp eq ge lt ne deriv_rtx deriv_rty deriv_rtx_coarse deriv_rtx_fine deriv_rty_coarse deriv_rty_fine dadd ddiv dmin dmax dmul dfma drcp deq dge dlt dne");
        static readonly HashSet<string> signed = Set("iadd ieq ige ilt imad imax imin imul ine ineg ishl ishr ibfe");
        static readonly HashSet<string> unsigned = Set("udiv uge ult umad umax umin umul ushr ubfe bfi and or xor not countbits firstbit_hi firstbit_lo firstbit_shi reversebits uaddc usubb");
        static readonly HashSet<string> sample = Set("sample sample_b sample_c sample_c_lz sample_d sample_l gather4 gather4_c gather4_po gather4_po_c");
        static readonly HashSet<string> loads = Set("ld ld_ms ld_uav_typed ld_raw ld_structured");
        static readonly HashSet<string> dynamicFlow = Set("if else endif loop endloop breakc continuec callc retc switch case default endswitch discard");
        static readonly HashSet<string> staticFlow = Set("break continue call ret label");
        static readonly HashSet<string> other = Set("mov movc swapc dmov dmovc ftoi ftou itof utof dtof ftod f16tof32 f32tof16 itod utod dtoi dtou resinfo bufinfo lod sample_info sample_pos nop emit cut emitthencut emit_stream cut_stream emitthencut_stream sync store_uav_typed store_raw store_structured eval_snapped eval_sample_index eval_centroid interface_call");
        static readonly Regex opcode = new Regex(@"^([a-z][a-z0-9_]*)(?:\s|\(|$)", RegexOptions.Compiled);
        internal static bool TryCount(string assembly, out CascadeShaderInstructions result, out string reason)
        {
            uint fp = 0, si = 0, ui = 0, total = 0, samples = 0, load = 0, stat = 0, dyn = 0, registers = 0;
            foreach (string line in assembly.Split('\n'))
            {
                string text = line.Trim(); if (text.Length == 0 || text.StartsWith("//")) continue;
                var match = opcode.Match(text); if (!match.Success) { result = default; reason = "无法识别 DXBC 汇编行：" + text; return false; }
                string op = match.Groups[1].Value;
                if (Regex.IsMatch(op, @"^(vs|ps|gs|hs|ds|cs)_\d_\d$")) continue;
                if (op == "dcl_temps")
                { uint.TryParse(text.Substring(op.Length).Trim(), out registers); continue; }
                if (op.StartsWith("dcl_")) continue;
                if (op.EndsWith("_sat")) op = op.Substring(0, op.Length - 4);
                if (op.EndsWith("_nz")) op = op.Substring(0, op.Length - 3);
                else if (op.EndsWith("_z")) op = op.Substring(0, op.Length - 2);
                int offset = op.IndexOf("_aoffimmi", StringComparison.Ordinal); if (offset >= 0) op = op.Substring(0, offset);
                if (op.EndsWith("_indexable")) op = op.Substring(0, op.Length - 10);
                total++;
                if (floating.Contains(op)) fp++;
                else if (signed.Contains(op)) si++;
                else if (unsigned.Contains(op)) ui++;
                else if (sample.Contains(op)) samples++;
                else if (loads.Contains(op)) load++;
                else if (dynamicFlow.Contains(op)) dyn++;
                else if (staticFlow.Contains(op)) stat++;
                else if (!other.Contains(op)) { result = default; reason = "暂未分类的 DXBC 指令：" + op; return false; }
            }
            result = new CascadeShaderInstructions(fp, si, ui, total, samples, load, stat, dyn, registers);
            reason = total == 0 ? "没有可统计的 DXBC 指令" : "统计来源：DXBC 反汇编分类（Unity 已剥离 STAT）。向量指令计一次；结构化分支按静态出现次数计。";
            return total > 0;
        }
    }
}
