using System;
using UnityEngine;
using Verse;

namespace IsoRimCEPack
{
    // The pack's turret models keep their shape on a footprint of another shape.
    //
    // IsoRim fits a model to its thing's footprint (ModelDef.builtFor, ModelEntry.FootprintFit): x and z stretched to the
    // footprint, y by their geometric mean, read every time it is drawn. A turret's gun turns, so a model stretched more one
    // way than the other draws a gun whose length changes as it turns, on a base pulled out of shape. When another mod gives
    // a turret our model is made for a footprint of another shape (e.g. "The Generic Ammo Experience for Combat Extended"
    // makes Turret_Heavy 2x1), the model is fitted uniformly instead: its builtFor becomes the footprint's own shape at the
    // model's size (footprint / u), so IsoRim scales it by u on every axis, centred on the footprint as before. u = the
    // smaller of the two side ratios (UniformScale): the largest size at which the model still stands within its cells.
    // Decided by the thing def's size against the model's builtFor only. A footprint of the model's own shape (every turret
    // no mod resizes, and a mod scaling one evenly) is left alone, so those draw exactly as before. Only turret models
    // (topModel) of this pack with a square builtFor (all of them): a non-square base is IsoRim's to turn and fit.
    [StaticConstructorOnStartup]
    internal static class TurretModels
    {
        internal static int Refitted;   // for the harness

        static TurretModels()
        {
            try
            {
                ModContentPack pack = LoadedModManager.GetMod<CEPackMod>()?.Content;
                foreach (IsoRim.ModelDef md in DefDatabase<IsoRim.ModelDef>.AllDefsListForReading)
                {
                    if (pack == null || md.modContentPack != pack || md.topModel.NullOrEmpty() || md.thingDef.NullOrEmpty()
                        || !md.style.NullOrEmpty() || md.builtFor.x <= 0f || !Mathf.Approximately(md.builtFor.x, md.builtFor.y))
                    {
                        continue;
                    }
                    ThingDef t = DefDatabase<ThingDef>.GetNamedSilentFail(md.thingDef);
                    if (t == null)
                    {
                        continue;
                    }
                    float rx = t.size.x / md.builtFor.x;
                    float rz = t.size.z / md.builtFor.y;
                    if (Mathf.Approximately(rx, rz))
                    {
                        continue;   // the model's own shape: IsoRim's fit is uniform already
                    }
                    float u = UniformScale(rx, rz);
                    Vector2 was = md.builtFor;
                    md.builtFor = new Vector2(t.size.x / u, t.size.z / u);
                    Refitted++;
                    if (Harness.On)
                    {
                        Log.Message(Pack.Tag + "turret model " + md.defName + ": " + t.defName + " is " + t.size.x + "x" + t.size.z
                            + ", model built for " + was + ": fitted uniformly x" + u.ToString("0.###") + " (builtFor " + md.builtFor + ")");
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warning(Pack.Tag + "Turret models on a resized footprint not refitted (they stretch to it): " + e);
            }
        }

        // The one scale a turret model takes on a footprint of another shape: the smaller side ratio, so it stays within
        // the footprint.
        private static float UniformScale(float rx, float rz)
        {
            return Mathf.Min(rx, rz);
        }
    }
}
