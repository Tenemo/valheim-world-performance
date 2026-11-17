using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace WorldPerformance
{
    // Drawing merged meshes into the sky depth camera's view.
    internal static partial class SpatialBuildings
    {
        private static Camera SkyCamera(DepthCamera depth)
        {
            var camera = depth.GetComponent<Camera>();
            skyDepthCamera = depth;
            if (camera == skyCamera) return camera;
            if (skyCamera) skyCamera.RemoveCommandBuffer(CameraEvent.AfterForwardOpaque, skyCommands);
            skyCamera = camera;
            camera.AddCommandBuffer(CameraEvent.AfterForwardOpaque, skyCommands);
            // An orthographic camera selects detail levels from its size alone, wherever it is.
            skyScale = SkyDetailBias * .5f / camera.orthographicSize;
            ForgetSky();
            return camera;
        }

        // The sky slots follow that camera's detail levels, and are filled again where it draws next.
        private static void ForgetSky()
        {
            foreach (var cell in cells.Values) foreach (var batch in cell.Batches) batch.SkyData = null;
            skyScanned = false;
        }

        // Where the sky depth camera renders from with the player or the main camera at the given position, as RenderDepth
        // places it.
        private static Vector3 SkyPosition(DepthCamera depth, Vector3 position)
        {
            position += Vector3.up * depth.m_offset;
            return new Vector3(Mathf.Round(position.x), Mathf.Round(position.y), Mathf.Round(position.z));
        }

        // Once a second the game's sky depth camera draws pieces from above with a replacement shader, for rain and snow
        // under roofs. Merged meshes draw in that render through the derived depth shader instead, from their sky slots, which
        // draw each merged member at the levels that camera selects, since the meshes hold them all. The slots of meshes it
        // can reach are prepared within the frame budget beforehand, and only the merged renderers on layers it sees are
        // switched off while it renders.
        private static void BeforeSkyDepth(DepthCamera __instance)
        {
            if (!Running || Native) return;
            using (new Timed(SkyDepth))
                try
                {
                    var main = Utils.GetMainCamera();
                    if (!main) return;
                    var camera = SkyCamera(__instance);
                    var box = SkyBox(camera, SkyPosition(__instance, Player.m_localPlayer ? Player.m_localPlayer.transform.position : main.transform.position));
                    skyDepth = true;
                    skyMask = camera.cullingMask;
                    skyCommands.Clear();
                    foreach (var cell in cells.Values)
                        foreach (var batch in cell.Batches)
                        {
                            if (!batch.Mesh || !batch.Renderer || !batch.Bounds.Intersects(box) || !SkyHides(batch, true)) continue;
                            batch.SkyHidden = true;
                            skyBatches.Add(batch);
                            if ((skyMask & 1 << batch.Key.Layer) == 0 || !SkyDrawn(batch, __instance.m_depthShader)) continue;
                            UploadSky(batch);
                            skyCommands.DrawMesh(batch.Mesh, Matrix4x4.identity, skyMaterial, 1, 0, batch.SkyProperties);
                        }
                }
                catch (Exception error) { Fail(error); }
        }

        private static Exception AfterSkyDepth(Exception __exception)
        {
            if (!skyDepth) return __exception;
            skyDepth = false;
            using (new Timed(SkyDepth))
                try
                {
                    skyCommands?.Clear();
                    // A flush during the render may have changed what a mesh draws, so its renderers follow its drawn range again.
                    foreach (var batch in skyBatches) { batch.SkyHidden = false; SkyHides(batch, false); ShowDrawn(batch); }
                }
                catch (Exception error) { Fail(error); }
                finally { skyBatches.Clear(); }
            return __exception;
        }

        // Switches a merged mesh's renderers on layers the sky depth camera sees off for its render, and back on; false when it
        // sees none of them. The replacement render would draw a merged mesh at its local vertices.
        private static bool SkyHides(Batch batch, bool hide)
        {
            bool seen = (skyMask & 1 << batch.Key.Layer) != 0, twin = batch.Twin && (skyMask & 1 << TwinLayerOf(batch.Key.Layer)) != 0, ranges = (skyMask & RangeLayers) != 0;
            if (!seen && !twin && !ranges) return false;
            if (seen && batch.Renderer) batch.Renderer.forceRenderingOff = hide;
            if (twin) batch.Twin.forceRenderingOff = hide;
            if (ranges) foreach (var range in batch.Lights) if (range.Renderer) range.Renderer.forceRenderingOff = hide;
            return true;
        }

        // Gives a merged mesh drawn by the sky depth camera its sky slots and their buffer, and uploads the slots changed since.
        private static void UploadSky(Batch batch)
        {
            if (batch.SkyData == null) FillSky(batch);
            if (batch.SkyTransforms == null)
            {
                batch.SkyTransforms = new GraphicsBuffer(GraphicsBuffer.Target.Constant, SlotLimit * 2, 64);
                batch.SkyProperties.SetConstantBuffer(skySlotsId, batch.SkyTransforms, 0, SlotLimit * 128);
            }
            if (batch.SkyTo >= 0) batch.SkyTransforms.SetData(batch.SkyData, batch.SkyFrom * 2, batch.SkyFrom * 2, (batch.SkyTo - batch.SkyFrom + 1) * 2);
            batch.SkyFrom = int.MaxValue;
            batch.SkyTo = -1;
        }

        // Prepares, within the frame's budget, the sky slots of new and rebuilt meshes and of those the player has come near,
        // which the sky depth camera's next renders can reach: within a cell of its view from the player's position now.
        private static void PrepareSky()
        {
            if (!skyCamera || !skyDepthCamera || Native) return;
            var player = Player.m_localPlayer ? Player.m_localPlayer.transform.position : lastCamera;
            var reach = SkyBox(skyCamera, SkyPosition(skyDepthCamera, player));
            reach.Expand(CellSize * 2f);
            if ((!skyScanned || (player - skyScanFrom).sqrMagnitude > SkyScanStep * SkyScanStep) && Open())
            {
                skyScanned = true;
                skyScanFrom = player;
                foreach (var cell in cells.Values)
                    foreach (var batch in cell.Batches)
                        if (!batch.SkyQueued && batch.Mesh && (batch.SkyData == null || batch.SkyTransforms == null) && batch.Bounds.Intersects(reach)) QueueSky(batch);
            }
            while (skyPending.Count > 0 && Open())
            {
                var batch = skyPending.Dequeue();
                batch.SkyQueued = false;
                if (batch.Mesh && batch.Renderer && (skyCamera.cullingMask & 1 << batch.Key.Layer) != 0 && SkyDrawn(batch, skyDepthCamera.m_depthShader) &&
                    batch.Bounds.Intersects(reach))
                    UploadSky(batch);
            }
        }

        private static void QueueSky(Batch batch)
        {
            if (batch.SkyQueued) return;
            batch.SkyQueued = true;
            skyPending.Enqueue(batch);
        }

        // Whether the replacement render draws the batch's native material: only when one of its subshaders has the same
        // render type, as for the native renderers. Layers the sky camera leaves out are skipped by the caller.
        private static bool SkyDrawn(Batch batch, Shader replacement)
        {
            if (replacement != skyReplacement)
            {
                skyReplacement = replacement;
                skyTypes.Clear();
                if (replacement)
                    for (int index = 0; index < replacement.subshaderCount; index++) skyTypes.Add(replacement.FindSubshaderTagValue(index, renderType).name ?? "");
            }
            if (batch.SkyType == null) batch.SkyType = batch.Material ? batch.Material.GetTag("RenderType", false) ?? "" : "";
            return skyTypes.Contains(batch.SkyType);
        }

        // The world-space box the orthographic sky depth camera sees from the given position, with a margin.
        private static Bounds SkyBox(Camera camera, Vector3 position)
        {
            var rotation = camera.transform.rotation;
            float height = camera.orthographicSize, width = height * camera.aspect;
            var box = new Bounds(position + rotation * new Vector3(-width, -height, camera.nearClipPlane), Vector3.zero);
            for (int corner = 1; corner < 8; corner++)
                box.Encapsulate(position + rotation * new Vector3((corner & 1) == 0 ? -width : width, (corner & 2) == 0 ? -height : height,
                    (corner & 4) == 0 ? camera.nearClipPlane : camera.farClipPlane));
            box.Expand(2f);
            return box;
        }
    }
}
