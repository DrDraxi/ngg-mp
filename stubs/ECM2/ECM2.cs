// Compile-time stub of the game's ECM2.dll (Easy Character Movement 2). Members as documented in docs/gamemap and
// confirmed against the real assembly (docs/confirm answers #4, #63, #78); bodies throw. Never shipped.
using UnityEngine;

namespace ECM2
{
    public class Character : MonoBehaviour
    {
        // At the feet (answers #55).
        public Vector3 GetPosition() { throw null; }
        public void SetPosition(Vector3 position, bool updateGround = false) { throw null; }
        public void TeleportPosition(Vector3 newPosition, bool interpolating = true, bool updateGround = false) { throw null; }
        public void TeleportRotation(Quaternion newRotation, bool interpolating = true) { throw null; }
        public Quaternion GetRotation() { throw null; }
        public void SetRotation(Quaternion newRotation) { throw null; }
        public void SetMovementDirection(Vector3 movementDirection) { throw null; }
        public Vector3 GetVelocity() { throw null; }
        public bool IsOnGround() { throw null; }
        public virtual bool IsCrouched() { throw null; }
        public void Crouch() { throw null; }
        public void UnCrouch() { throw null; }
        public bool IsJumping() { throw null; }
    }

    public class CharacterMovement : MonoBehaviour
    {
    }
}
