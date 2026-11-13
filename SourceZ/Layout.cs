using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CombatExtended;
using UnityEngine;
using Verse;

namespace IsoRimCEPackZ
{
    // A floored map's frames: the storeys' bands (rows), read once per map from ProjectZ's Api (floors are fixed for a map).
    // Frame k = floor k's band: rows [Shift(k), Shift(k) + Rows), heights in CE units over that floor's floor.
    // Virtual space (ProjectZ Api 6): x, z over the ground rows, y = absolute height in cells (floor k at k * StoreyHeight).
    internal sealed class Layout
    {
        public const int None = int.MinValue;

        public Map map;
        public int min, max;      // lowest and highest floor
        public int[] shift;       // rows from the ground rows to floor k's rows (index k - min)
        public int rows;          // ground rows per band
        public int width;

        private static readonly ConditionalWeakTable<Map, Layout> layouts = new ConditionalWeakTable<Map, Layout>();
        private static readonly Layout NoFloors = new Layout();
        private static Map lastMap;
        private static Layout last;

        // CE height units per storey: a storey (StoreyHeight cells) in CE units, with the pack's mapping
        // cells = ce * WallHeight / CollisionVertical.WallCollisionHeight (D9a: by wall, 2 CE units = one wall).
        public static float S => ProjectZ.Api.StoreyHeight * CollisionVertical.WallCollisionHeight / ProjectZ.Api.WallHeight;

        // Cells per CE height unit.
        public static float CellsPerUnit => ProjectZ.Api.WallHeight / CollisionVertical.WallCollisionHeight;

        // ProjectZ's aim heights on a target cell, in cells over its floor, in the order tried (head, chest, waist):
        // ProjectZ Api 10 AimHeights, read once.
        public static IReadOnlyList<float> AimHeights => aimHeights ??= ProjectZ.Api.Version >= 10 ? ReadAimHeights() : FallbackAimHeights;

        private static IReadOnlyList<float> aimHeights;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IReadOnlyList<float> ReadAimHeights() => ProjectZ.Api.AimHeights;

        // Fallback for a ProjectZ older than Api 10 (no getter there): its Los3D aim heights of that time.
        private static readonly float[] FallbackAimHeights = { 1.10f, 0.80f, 0.50f };

        // The layout of a map with floors; null without floors (or without a map).
        public static Layout Of(Map map)
        {
            if (map == null)
            {
                return null;
            }
            if (map == lastMap)
            {
                return last;
            }
            Layout l = layouts.GetValue(map, Build);
            lastMap = map;
            last = l == NoFloors ? null : l;
            return last;
        }

        private static Layout Build(Map map)
        {
            if (!ProjectZ.Api.HasFloors(map))
            {
                return NoFloors;
            }
            Layout l = new Layout { map = map, width = map.Size.x, rows = ProjectZ.Api.GroundRect(map).maxZ + 1 };
            int lo = 0, hi = 0;
            while (lo > -32 && ProjectZ.Api.CellOnFloor(IntVec3.Zero, lo - 1, map).IsValid)
            {
                lo--;
            }
            while (hi < 64 && ProjectZ.Api.CellOnFloor(IntVec3.Zero, hi + 1, map).IsValid)
            {
                hi++;
            }
            l.min = lo;
            l.max = hi;
            l.shift = new int[hi - lo + 1];
            for (int k = lo; k <= hi; k++)
            {
                l.shift[k - lo] = ProjectZ.Api.FloorRowShift(k, map);
            }
            return l;
        }

        public int Shift(int k) => shift[k - min];

        public bool Has(int k) => k >= min && k <= max;

        // The floor whose band holds row z; None for a void row, south of the ground or north of the top band.
        public int FrameOfRow(int z)
        {
            for (int k = min; k <= max; k++)
            {
                int s = shift[k - min];
                if (z >= s && z < s + rows)
                {
                    return k;
                }
            }
            return None;
        }

        public int FrameOf(IntVec3 c) => FrameOfRow(c.z);

        public int FrameOf(Vector3 v) => FrameOfRow(Mathf.FloorToInt(v.z));

        // The ground cell of a band cell (Invalid for a void row).
        public IntVec3 Ground(IntVec3 c)
        {
            int k = FrameOf(c);
            return k == None ? IntVec3.Invalid : new IntVec3(c.x, 0, c.z - Shift(k));
        }

        // The cell over ground cell g on floor k (Invalid for a floor the map doesn't have).
        public IntVec3 Cell(IntVec3 g, int k) => Has(k) ? new IntVec3(g.x, 0, g.z + Shift(k)) : IntVec3.Invalid;

        // A point of frame 'from' seen in frame 'to': rows moved, height (CE units) lowered by the storeys between.
        public Vector3 Translate(Vector3 v, int from, int to)
        {
            if (from == to || from == None || to == None)
            {
                return v;
            }
            v.z += Shift(to) - Shift(from);
            v.y -= (to - from) * S;
            return v;
        }

        // Only the rows (a flat point, a cell centre): the height is left as it is.
        public Vector3 TranslateRows(Vector3 v, int from, int to)
        {
            if (from == to || from == None || to == None)
            {
                return v;
            }
            v.z += Shift(to) - Shift(from);
            return v;
        }

        // A frame point (CE units over floor k) as a virtual point (cells over the ground's floor; ProjectZ Api 6).
        public Vector3 ToVirtual(Vector3 v, int k) => new Vector3(v.x, k * ProjectZ.Api.StoreyHeight + v.y * CellsPerUnit, v.z - Shift(k));

        // A virtual point seen in frame k (CE units over floor k; the inverse of ToVirtual).
        public Vector3 FromVirtual(Vector3 v, int k) => new Vector3(v.x, (v.y - k * ProjectZ.Api.StoreyHeight) / CellsPerUnit, v.z + Shift(k));

        // The ground rows hold ground row gz.
        public bool OnGround(float gz) => gz >= 0f && gz < rows;
    }
}
