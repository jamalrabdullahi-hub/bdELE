using UnityEngine;

namespace BDArmory.Avionics
{
    /// <summary>
    /// One editor lock for all ELE windows in the VAB/SPH: on only while the mouse is really over a visible ELE window
    /// (so clicks there don't place parts), off otherwise - Launch / Save can never stay locked by a closed or collapsed window.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class EditorLockGuard : MonoBehaviour
    {
        const string LockId = "ELE_EditorUI";
        bool locked;

        void LateUpdate()
        {
            if (EditorLogic.fetch == null) return;
            bool over = UIDock.MouseOverWindow();
            if (over == locked) return;
            if (over) EditorLogic.fetch.Lock(true, true, true, LockId); else EditorLogic.fetch.Unlock(LockId);
            locked = over;
        }

        void OnDestroy() { if (locked && EditorLogic.fetch != null) EditorLogic.fetch.Unlock(LockId); }
    }
}
