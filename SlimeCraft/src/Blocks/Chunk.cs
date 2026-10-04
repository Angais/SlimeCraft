using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SlimeCraft.BlocksMod
{
    /// <summary>Collider groups per chunk, each with its own PhysicMaterial (SR rigidbodies feel the difference).</summary>
    internal enum PhysGroup { Normal = 0, Bouncy = 1, Slippery = 2, Sticky = 3 }

    /// <summary>
    /// 16x16x16 block storage + the Unity objects that render/collide it.
    /// ids are palette indices (0 = air); facing holds a <see cref="BlockFacing"/> per block.
    /// </summary>
    internal sealed class Chunk
    {
        public const int Size = 16;
        public const int Volume = Size * Size * Size;

        public readonly int Cx, Cy, Cz;
        public readonly long Key;
        public readonly ushort[] Ids = new ushort[Volume];
        public readonly byte[] Facing = new byte[Volume];
        public int Count;

        public bool RenderDirty, ColliderDirty;
        public int FailCount;

        // Unity objects (created lazily, destroyed with the chunk)
        public GameObject Go;
        public MeshFilter OpaqueMF, TransMF, EmisMF;
        public MeshRenderer OpaqueMR, TransMR, EmisMR;
        /// <summary>Opaque+cutout faces (lit), translucent faces (sorted), light-emitting faces (full-bright material).</summary>
        public Mesh OpaqueMesh, TransMesh, EmisMesh;
        public readonly GameObject[] ColGo = new GameObject[4];
        public readonly MeshCollider[] Col = new MeshCollider[4];
        public readonly Mesh[] ColMesh = new Mesh[4];

        // Translucent back-to-front sorting data (chunk-local quad centers).
        public readonly List<Vector3> TransCenters = new List<Vector3>();
        public float[] SortKeys = new float[0];
        public int[] SortOrder = new int[0];
        public Vector3 LastSortCam = new Vector3(float.MaxValue, 0, 0);
        public bool HasTranslucent;

        public Chunk(int cx, int cy, int cz)
        {
            Cx = cx; Cy = cy; Cz = cz;
            Key = BlockWorld.ChunkKey(cx, cy, cz);
        }

        public Vector3 Origin => new Vector3(Cx * Size, Cy * Size, Cz * Size);
        public Vector3 Center => Origin + new Vector3(8, 8, 8);

        public static int Index(int lx, int ly, int lz) => (ly << 8) | (lz << 4) | lx;

        public static Mesh NewMesh(string name)
        {
            var m = new Mesh { name = name };
            m.indexFormat = IndexFormat.UInt32; // a full 16^3 chunk can exceed 65k vertices
            m.MarkDynamic();
            return m;
        }

        public void DestroyObjects(HashSet<int> colliderIds)
        {
            for (int i = 0; i < 4; i++)
            {
                // ReferenceEquals: also forget colliders Unity already destroyed (instance ids stay readable)
                if (!ReferenceEquals(Col[i], null)) colliderIds.Remove(Col[i].GetInstanceID());
                if (ColMesh[i] != null) Object.Destroy(ColMesh[i]);
                Col[i] = null; ColGo[i] = null; ColMesh[i] = null;
            }
            if (OpaqueMesh != null) Object.Destroy(OpaqueMesh);
            if (TransMesh != null) Object.Destroy(TransMesh);
            if (EmisMesh != null) Object.Destroy(EmisMesh);
            if (Go != null) Object.Destroy(Go);
            Go = null; OpaqueMesh = null; TransMesh = null; EmisMesh = null;
            OpaqueMF = TransMF = EmisMF = null; OpaqueMR = TransMR = EmisMR = null;
            HasTranslucent = false;
            TransCenters.Clear();
        }
    }
}
