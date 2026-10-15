using System;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Places an idle/resting loaded Worker onto the explicit RestPoint authored by its home piece.
    /// Navigation still owns travel to the HomeAnchor; this layer only performs the final local
    /// furniture placement once the Worker is already home. Since v0.5.150 this includes the
    /// Greydwarf family: their root stump uses a natural standing/idle pose instead of a synthetic sit.
    /// </summary>
    internal static class WorkerRestStationPresentationRuntime
    {
        internal static bool TryMaintain(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null) return false;
            WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            if (state != WorkerState.Resting && state != WorkerState.Recover && state != WorkerState.WaitAtHome)
                return false;

            Vector3 home = WorkerZdo.ReadHome(zdo, worker.transform.position);
            if (Vector3.Distance(worker.transform.position, home) > WorkerRestRuntime.HomeRestDistance + 2.5f)
                return false;

            WorkerRestStation station = WorkerRestStation.ResolveLoadedForWorker(zdo);
            Transform rest = station != null ? station.RestPoint : null;
            if (rest == null) return false;

            ZNetView nview = worker.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) return false;

            // RestPoints are deliberately authored on the Worker pieces (bed surface, root-stump centre,
            // nest center, landing pad, etc.). The old AI stopped up to 3 m from the HomeAnchor;
            // snapping only this final short furniture step makes the Worker visibly USE the home
            // without touching the stable pathfinder.
            worker.transform.position = rest.position;

            Vector3 forward = Vector3.ProjectOnPlane(rest.forward, Vector3.up);
            if (forward.sqrMagnitude > 0.0001f)
            {
                forward.Normalize();
                worker.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);

                // v0.5.161: MonsterAI is still allowed to tick while a Worker rests so combat and
                // awareness keep behaving like vanilla. That AI can, however, continuously change
                // Character.m_lookDir while our furniture layer fixes the body rotation. The two
                // rotations then fight each other in the animator and produce the visible left/right
                // twitch seen at Greydwarf root nests. Own the final presentation look direction as
                // well as the transform while the Worker is actually on its RestPoint.
                try { worker.SetLookDir(forward, 0f); }
                catch { }
            }

            worker.SetMoveDir(Vector3.zero);
            worker.SetRun(false);

            // Furniture/rest presentation owns the final local pose. Kill residual physics velocity
            // so an invisible placement collider, slope or a previous movement frame cannot shove a
            // resting creature back out of its authored RestPoint. Normal physics resumes as soon as
            // the Worker leaves Resting/Recover/WaitAtHome.
            try
            {
                Rigidbody body = worker.GetComponent<Rigidbody>();
                if (body != null)
                {
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }
            }
            catch { }

            return true;
        }
    }
}
