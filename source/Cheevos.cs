/*
 * SymphonyRecomp Achievements add-on
 *
 * (c) Copyright mxd, kia100
 *
 * See LICENSE in the root directory for more info.
 */

using System;
using System.Collections.Generic;
using System.Numerics;

namespace SotnAchievements;

// little evaluator for RetroAchievements MemAddr strings. I just paste the raw condition string
// from the RA set and it tells me each frame if the cheevo popped, no runtime dep on RA itself.
// only bothered with the operators this game (11240) actually uses - no addaddress or ornext.
// if some future set needs those just add them below. syntax is the usual rcheevos one.
public static class Cheevos
{
    public enum Sz   { U8, U16, U32, U24, BE16, BE24, BE32, LoNib, HiNib, Bit0, Bit1, Bit2, Bit3, Bit4, Bit5, Bit6, Bit7, BitCount }
    public enum Pre  { Cur, Delta, Prior, Bcd }
    public enum Flag { Std, AddSource, SubSource, AndNext, AddHits, SubHits, PauseIf, ResetIf, ResetNextIf, Measured, MeasuredIf, Trigger }
    public enum Cmp  { Eq, Ne, Lt, Le, Gt, Ge, None }

    // a single memory read. we hang onto last/prior so the delta and prior operands work
    public sealed class MemRef
    {
		public readonly uint Addr;
        public readonly      Sz Size;
        public uint          Cur, Last, Prior;

        bool _primed;
        public MemRef(Sz s, uint a) { Size = s; Addr = a; }

        public void Sample(Func<uint, byte> rb)
        {
            Cur = Read(rb);
            if (!_primed) { Last = Prior = Cur; _primed = true; } // dont let a delta fire on the very first frame
        }

        public void Commit()
        {
            if (Cur != Last) Prior = Last;
            Last = Cur;
        }

        uint Read(Func<uint, byte> rb)
        {
            byte b = rb(Addr);
            switch (Size)
            {
                case Sz.U8:       return b;
                case Sz.U16:      return (uint)(b | rb(Addr + 1) << 8);
                case Sz.U24:      return (uint)(b | rb(Addr + 1) << 8 | rb(Addr + 2) << 16);
                case Sz.U32:      return (uint)(b | rb(Addr + 1) << 8 | rb(Addr + 2) << 16 | rb(Addr + 3) << 24);
                case Sz.BE16:     return (uint)(b << 8 | rb(Addr + 1));
                case Sz.BE24:     return (uint)(b << 16 | rb(Addr + 1) << 8 | rb(Addr + 2));
                case Sz.BE32:     return (uint)(b << 24 | rb(Addr + 1) << 16 | rb(Addr + 2) << 8 | rb(Addr + 3));
                case Sz.LoNib:    return (uint)(b & 0x0F);
                case Sz.HiNib:    return (uint)(b >> 4);
                case Sz.BitCount: return (uint)BitOperations.PopCount(b);
                default:          return (uint)(b >> (Size - Sz.Bit0) & 1); // Bit0..Bit7
            }
        }
    }

    // either a memory read (with a delta/prior/bcd flavour) or a plain constant
    public sealed class Operand
    {
        public readonly bool    IsConst;
        public readonly long    Const;
        public readonly MemRef? Mem;
        public readonly Pre     Prefix;

        public Operand(long c)
        {
            IsConst = true;
            Const = c;
        }

        public Operand(MemRef m, Pre p)
        {
            Mem = m;
            Prefix = p;
        }

        public long Value()
        {
            if (IsConst) return Const;

            uint v = Prefix switch { Pre.Delta => Mem!.Last, Pre.Prior => Mem!.Prior, _ => Mem!.Cur };

            if (Prefix == Pre.Bcd)
                v = (uint)((v & 0xF) + (v >> 4 & 0xF) * 10 + (v >> 8 & 0xF) * 100 + (v >> 12 & 0xF) * 1000);
            return v;
        }
    }

    public sealed class Cond
    {
        public Flag     Flag;
        public Operand  L = null!;
        public Cmp      Op;
        public Operand? R;

        public int HitTarget;
        public int Hits;
    }

    public sealed class Trigger
    {
        readonly List<MemRef>                   _refs   = new();
        readonly Dictionary<(Sz, uint), MemRef> _cache  = new(); // one MemRef per addr+size so delta state is shared
        readonly List<List<Cond>>               _groups = new(); // [0] is core, the rest are alt groups

        MemRef Get(Sz s, uint a)
        {
            if (_cache.TryGetValue((s, a), out var r)) return r;
            r = new MemRef(s, a);

            _cache[(s, a)] = r;
            _refs.Add(r);

            return r;
        }

        public static Trigger Parse(string memaddr)
        {
            var t = new Trigger();

            foreach (var grp in SplitGroups(memaddr))
            {
                var conds = new List<Cond>();

                foreach (var part in grp.Split('_'))
                    if (part.Length > 0)
                        conds.Add(t.ParseCond(part.Trim()));

                if (conds.Count > 0) t._groups.Add(conds);
            }

            return t;
        }

        // groups split on 'S'. thing is 'S' is also the bit6 size char so dont split on a "0xS..." ref
        static IEnumerable<string> SplitGroups(string s)
        {
            int start = 0;

            for (int i = 0; i < s.Length; i++)
                if (s[i] == 'S' && !(i >= 2 && s[i - 1] == 'x' && s[i - 2] == '0'))
                {
                    yield return s[start..i];
                    start = i + 1;
                }

            yield return s[start..];
        }

        Cond ParseCond(string s)
        {
            var c = new Cond();

            if (s.Length >= 2 && s[1] == ':') // leading flag, "P:" / "A:" / ...
            {
                c.Flag = s[0] switch
                {
                    'A' => Flag.AddSource,  'B' => Flag.SubSource,   'N' => Flag.AndNext,
                    'C' => Flag.AddHits,    'D' => Flag.SubHits,     'P' => Flag.PauseIf,
                    'R' => Flag.ResetIf,    'Z' => Flag.ResetNextIf, 'M' => Flag.Measured,
                    'Q' => Flag.MeasuredIf, 'T' => Flag.Trigger,      _  => Flag.Std,
                };

                s = s[2..];
            }

            int dot = s.IndexOf('.'); // trailing hit target ".N."
            if (dot >= 0 && s.EndsWith('.') && int.TryParse(s[(dot + 1)..^1], out int hits))
            {
                c.HitTarget = hits;
                s = s[..dot];
            }

            var (op, at, len) = FindOp(s);
            if (op == Cmp.None)
            {
                c.Op = Cmp.None;
                c.L = ParseOperand(s); // sources / measured with no compare
            }
            else
            {
                c.Op = op;
                c.L = ParseOperand(s[..at]);
                c.R = ParseOperand(s[(at + len)..]);
            }
            return c;
        }

        static (Cmp, int, int) FindOp(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (i + 1 < s.Length)
                {
                    var two = s.Substring(i, 2); // check the 2-char ones first
                    if (two == "!=") return (Cmp.Ne, i, 2);
                    if (two == "<=") return (Cmp.Le, i, 2);
                    if (two == ">=") return (Cmp.Ge, i, 2);
                }

                switch (s[i])
                {
                    case '=': return (Cmp.Eq, i, 1);
                    case '<': return (Cmp.Lt, i, 1);
                    case '>': return (Cmp.Gt, i, 1);
                }
            }
            return (Cmp.None, -1, 0);
        }

        Operand ParseOperand(string s)
        {
            s       = s.Trim();
            Pre pre = Pre.Cur;

            if (s.Length > 2 &&
                (s[0] == 'd' || s[0] == 'p' || s[0] == 'b') && s[1] == '0' && (s[2] == 'x' || s[2] == 'X'))
            {
                pre = (s[0] == 'd') ? Pre.Delta : (s[0] == 'p') ? Pre.Prior : Pre.Bcd;
                s   = s[1..];
            }

            // a memref is 0x + a size char + hex address. anything else is just a value
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && s.Length > 2 && IsSizeChar(s[2]))
                return new Operand(Get(SizeOf(s[2]), Convert.ToUInt32(s[3..], 16)), pre);

            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return new Operand(Convert.ToInt64(s[2..], 16));

            if (s.Length > 0 && (s[0] == 'h' || s[0] == 'H'))
                return new Operand(Convert.ToInt64(s[1..], 16));

            if (s.Length > 0 && (s[0] == 'f' || s[0] == 'F'))
                return new Operand((long)double.Parse(s[1..], System.Globalization.CultureInfo.InvariantCulture));

            return new Operand(long.Parse(s, System.Globalization.CultureInfo.InvariantCulture));
        }

        static bool IsSizeChar(char c) => c == ' ' || "HXWGIJLUMNOPQRSTK".IndexOf(char.ToUpperInvariant(c)) >= 0;

        static Sz SizeOf(char c) => char.ToUpperInvariant(c) switch
        {
            'H' => Sz.U8,    ' ' => Sz.U16,   'X' => Sz.U32,  'W' => Sz.U24,
            'I' => Sz.BE16,  'J' => Sz.BE24,  'G' => Sz.BE32,
            'L' => Sz.LoNib, 'U' => Sz.HiNib, 'K' => Sz.BitCount,
            'M' => Sz.Bit0,  'N' => Sz.Bit1,  'O' => Sz.Bit2, 'P' => Sz.Bit3,
            'Q' => Sz.Bit4,  'R' => Sz.Bit5,  'S' => Sz.Bit6, 'T' => Sz.Bit7, _ => Sz.U8,
        };

        // one frame. true the moment every requirement in the core group (and one alt, if there are alts) is met
        public bool Test(Func<uint, byte> rb)
        {
            foreach (var r in _refs) r.Sample(rb);

            bool ok = false;
            if (_groups.Count > 0)
            {
                bool core   = TestGroup(_groups[0]);
                bool anyAlt = (_groups.Count == 1); // no alts -> core alone is enough

                for (int i = 1; i < _groups.Count; i++)
                    if (TestGroup(_groups[i])) anyAlt = true; // still run every alt so its hits/delta stay ticking

                ok = core && anyAlt;
            }

            foreach (var r in _refs) r.Commit();
            return ok;
        }

        static bool Compare(long a, Cmp op, long b) => op switch
        {
            Cmp.Eq => a == b, Cmp.Ne  => a != b,
            Cmp.Lt => a < b,  Cmp.Le  => a <= b,
            Cmp.Gt => a > b,  Cmp.Ge  => a >= b, _ => a != 0,
        };

        static bool TestGroup(List<Cond> conds)
        {
            // heads up: real rcheevos does pause/reset in its own pass with fussier ordering than this
            // single loop. been fine for everything this set throws at it, but if a challenge cheevo ever
            // misbehaves (fires early, or never) this is the first spot i'd go poke at.
            long addBuffer   = 0; // AddSource/SubSource accumulator, folded into the next compare
            long addHits     = 0; // AddHits/SubHits accumulator
            bool andNext     = true;
            bool haveAndNext = false;
            bool resetNext   = false;
            bool paused      = false;
            bool reset       = false;
            bool allMet      = true;

            foreach (var c in conds)
            {
                long left = c.L.Value();

                if (c.Flag == Flag.AddSource) { addBuffer += left; continue; }
                if (c.Flag == Flag.SubSource) { addBuffer -= left; continue; }

                left      += addBuffer;
                addBuffer =  0;

                long right    = c.R?.Value() ?? 0;
                bool condTrue = Compare(left, c.Op, right);

                if (haveAndNext)
                {
                    condTrue    = andNext && condTrue;
                    haveAndNext = false;
                    andNext     = true;
                }

                if (c.Flag == Flag.AndNext)
                {
                    andNext     = condTrue;
                    haveAndNext = true;
                    continue;
                }

                if (c.Flag == Flag.ResetNextIf)
                {
                    if (condTrue) resetNext = true;
                    continue;
                }

                if (resetNext) { c.Hits = 0; resetNext = false; }

                if (c.Flag == Flag.AddHits)
                {
                    if (condTrue && (c.HitTarget == 0 || c.Hits < c.HitTarget)) c.Hits++;
                    addHits += c.Hits;
                    continue;
                }

                if (c.Flag == Flag.SubHits)
                {
                    if (condTrue && (c.HitTarget == 0 || c.Hits < c.HitTarget)) c.Hits++;
                    addHits -= c.Hits;
                    continue;
                }

                if (condTrue && (c.HitTarget == 0 || c.Hits < c.HitTarget)) c.Hits++;
                long totalHits = c.Hits + addHits;
                addHits = 0;
                bool met = (c.HitTarget == 0) ? condTrue : (totalHits >= c.HitTarget);

                switch (c.Flag)
                {
                    case Flag.PauseIf: if (met) paused  = true;  break;
                    case Flag.ResetIf: if (met) reset   = true;  break;
                    default:           if (!met) allMet = false; break; // Std / Measured / MeasuredIf / Trigger all just have to be true
                }
            }

            if (reset)
            {
                foreach (var c in conds) c.Hits = 0; // reset zeroes the whole group's hit counts
                return false;
            }
            return !paused && allMet;
        }
    }
}
