using System.Collections.Generic;
using VRageMath;

namespace Digi.ProjectorPreview
{
    public class ProjectorAligner
    {
        static Dictionary<MyBlockOrientation, Vector3I> RotationLookup = new Dictionary<MyBlockOrientation, Vector3I>
        {
            { new MyBlockOrientation(Base6Directions.Direction.Forward, Base6Directions.Direction.Up), new Vector3I(0, 0, 0) },
            { new MyBlockOrientation(Base6Directions.Direction.Forward, Base6Directions.Direction.Right), new Vector3I(0, 0, 1) },
            { new MyBlockOrientation(Base6Directions.Direction.Forward, Base6Directions.Direction.Down), new Vector3I(2, 2, 0) },
            { new MyBlockOrientation(Base6Directions.Direction.Forward, Base6Directions.Direction.Left), new Vector3I(0, 0, -1) },
            { new MyBlockOrientation(Base6Directions.Direction.Backward, Base6Directions.Direction.Up), new Vector3I(2, 0, 0) },
            { new MyBlockOrientation(Base6Directions.Direction.Backward, Base6Directions.Direction.Right), new Vector3I(2, 0, 1) },
            { new MyBlockOrientation(Base6Directions.Direction.Backward, Base6Directions.Direction.Down), new Vector3I(0, 2, 0) },
            { new MyBlockOrientation(Base6Directions.Direction.Backward, Base6Directions.Direction.Left), new Vector3I(0, 2, 1) },
            { new MyBlockOrientation(Base6Directions.Direction.Up, Base6Directions.Direction.Forward), new Vector3I(2, 1, 0) },
            { new MyBlockOrientation(Base6Directions.Direction.Up, Base6Directions.Direction.Right), new Vector3I(1, -2, -1) },
            { new MyBlockOrientation(Base6Directions.Direction.Up, Base6Directions.Direction.Backward), new Vector3I(0, -1, 0) },
            { new MyBlockOrientation(Base6Directions.Direction.Up, Base6Directions.Direction.Left), new Vector3I(1, 0, -1) },
            { new MyBlockOrientation(Base6Directions.Direction.Down, Base6Directions.Direction.Forward), new Vector3I(2, 1, 2) },
            { new MyBlockOrientation(Base6Directions.Direction.Down, Base6Directions.Direction.Right), new Vector3I(-1, 2, -1) },
            { new MyBlockOrientation(Base6Directions.Direction.Down, Base6Directions.Direction.Backward), new Vector3I(2, -1, 0) },
            { new MyBlockOrientation(Base6Directions.Direction.Down, Base6Directions.Direction.Left), new Vector3I(-1, 0, -1) },
            { new MyBlockOrientation(Base6Directions.Direction.Left, Base6Directions.Direction.Forward), new Vector3I(1, 1, 2) },
            { new MyBlockOrientation(Base6Directions.Direction.Left, Base6Directions.Direction.Up), new Vector3I(-1, 0, 0) },
            { new MyBlockOrientation(Base6Directions.Direction.Left, Base6Directions.Direction.Backward), new Vector3I(0, -1, -1) },
            { new MyBlockOrientation(Base6Directions.Direction.Left, Base6Directions.Direction.Down), new Vector3I(-1, 2, 0) },
            { new MyBlockOrientation(Base6Directions.Direction.Right, Base6Directions.Direction.Forward), new Vector3I(0, 1, -1) },
            { new MyBlockOrientation(Base6Directions.Direction.Right, Base6Directions.Direction.Up), new Vector3I(1, 0, 0) },
            { new MyBlockOrientation(Base6Directions.Direction.Right, Base6Directions.Direction.Backward), new Vector3I(1, -1, 0) },
            { new MyBlockOrientation(Base6Directions.Direction.Right, Base6Directions.Direction.Down), new Vector3I(1, 2, 0) },
        };

        /// <summary>
        /// Calculates offset and rotation needed to align a projector with its holographic projection.
        ///
        /// The pivot point of a projection is the first element in its serialized CubeBlock list, and should
        /// be used as a reference block to rotate and offset from.
        /// </summary>
        /// <param name="referenceBlockMin">MyObjectBuilder_CubeGrid.Min field from reference block</param>
        /// <param name="projectorBlockMin">MyObjectBuilder_CubeGrid.Min field from the projector</param>
        /// <param name="projectorBlockOrientation">orientation of the projector</param>
        /// <param name="projectionOffset">calculated offset</param>
        /// <param name="projectionRotation">calculated rotation</param>
        public static void Align(Vector3I referenceBlockMin, Vector3I projectorBlockMin,
            MyBlockOrientation projectorBlockOrientation,
            out Vector3I projectionOffset, out Vector3I projectionRotation
        )
        {
            var targetRotate = RotationLookup[projectorBlockOrientation];
            var offsetVector = -referenceBlockMin + projectorBlockMin;

            Vector3 r = targetRotate * MathHelper.ToRadians(90f);
            var q = Quaternion.CreateFromYawPitchRoll(r.X, r.Y, r.Z);
            offsetVector = Vector3I.Transform(offsetVector, q);
            projectionOffset = offsetVector;
            projectionRotation = targetRotate;
        }
    }
}
