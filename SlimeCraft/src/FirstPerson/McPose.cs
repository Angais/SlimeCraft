using UnityEngine;

namespace SlimeCraft.FP
{
    /// <summary>
    /// A rigid transform in Minecraft's right-handed camera space (x right, y up, -z forward). Every operation is
    /// applied in the current local frame (translate, then rotate about the already rotated axes), which is how
    /// item and arm poses are usually described. Scales are never folded in: non-uniform scales are expressed as
    /// separate Unity transform nodes so the result stays exact (see HandRig). Unity's Quaternion.AngleAxis follows
    /// the standard right-hand rule, so rotations need no conversion inside this space.
    /// </summary>
    internal struct McPose
    {
        public Vector3 P;
        public Quaternion Q;

        public static McPose Identity
        {
            get { McPose p; p.P = Vector3.zero; p.Q = Quaternion.identity; return p; }
        }

        /// <summary>Moves the origin by (x, y, z) measured along the current local axes.</summary>
        public void Translate(float x, float y, float z)
        {
            P += Q * new Vector3(x, y, z);
        }

        /// <summary>Rotates about the current local +X axis (degrees, right-handed).</summary>
        public void RotX(float deg) { if (deg != 0f) Q = Q * Quaternion.AngleAxis(deg, Vector3.right); }
        /// <summary>Rotates about the current local +Y axis (degrees, right-handed).</summary>
        public void RotY(float deg) { if (deg != 0f) Q = Q * Quaternion.AngleAxis(deg, Vector3.up); }
        /// <summary>Rotates about the current local +Z axis (degrees, right-handed).</summary>
        public void RotZ(float deg) { if (deg != 0f) Q = Q * Quaternion.AngleAxis(deg, Vector3.forward); }
        public void Rotate(Quaternion q) { Q = Q * q; }

        /// <summary>Rotation of a model display entry: Rx * Ry * Rz (X outermost), angles in degrees.</summary>
        public static Quaternion EulerXYZ(Vector3 deg)
        {
            return Quaternion.AngleAxis(deg.x, Vector3.right) * Quaternion.AngleAxis(deg.y, Vector3.up) * Quaternion.AngleAxis(deg.z, Vector3.forward);
        }

        /// <summary>Rotation of an entity model part: Rz * Ry * Rx (Z outermost), angles in radians.</summary>
        public static Quaternion RotationZYX(float zRad, float yRad, float xRad)
        {
            return Quaternion.AngleAxis(zRad * Mathf.Rad2Deg, Vector3.forward)
                 * Quaternion.AngleAxis(yRad * Mathf.Rad2Deg, Vector3.up)
                 * Quaternion.AngleAxis(xRad * Mathf.Rad2Deg, Vector3.right);
        }

        /// <summary>
        /// Writes the pose as the local transform of a Unity node. Unity space = Minecraft space mirrored on Z
        /// (F = diag(1,1,-1)); a rigid MC transform (p, q) becomes (F p, F q F) = ((x, y, -z), (-qx, -qy, qz, qw)).
        /// </summary>
        public void ApplyTo(Transform t)
        {
            t.localPosition = new Vector3(P.x, P.y, -P.z);
            Quaternion q = new Quaternion(-Q.x, -Q.y, Q.z, Q.w);
            float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (m > 1e-6f) { q.x /= m; q.y /= m; q.z /= m; q.w /= m; } else q = Quaternion.identity;
            t.localRotation = q;
        }
    }
}
