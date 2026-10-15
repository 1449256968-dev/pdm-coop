using System.Linq;
using UnityEngine;

/// <summary>One movement sample. Does not include combat, dash or UI actions yet.</summary>
public struct PlayerMovementInput
{
    public float strafe;
    public float forward;
    public float yaw;
    public bool run;

    public PlayerMovementInput Sanitized()
    {
        return new PlayerMovementInput
        {
            strafe = Finite(strafe) ? Mathf.Clamp(strafe, -1f, 1f) : 0f,
            forward = Finite(forward) ? Mathf.Clamp(forward, -1f, 1f) : 0f,
            yaw = Finite(yaw) ? Mathf.Repeat(yaw, 360f) : 0f,
            run = run
        };
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    // Preserve the original bindings, inversion and keyboard/controller mixing.
    public static PlayerMovementInput CaptureLocal(float yaw)
    {
        bool left = Held(HoPInput.StrafeLeft);
        bool right = Held(HoPInput.StrafeRight);
        bool forwards = Held(HoPInput.MoveForwards);
        bool backwards = Held(HoPInput.MoveBackwards);
        float x = Input.GetAxis("ThumbstickAxis" + GameSystem.settings.moveXAxis) *
                  (GameSystem.settings.invertXMoveAxis ? -1f : 1f);
        float y = Input.GetAxis("ThumbstickAxis" + GameSystem.settings.moveYAxis) *
                  (GameSystem.settings.invertYMoveAxis ? -1f : 1f);
        return new PlayerMovementInput
        {
            strafe = Mathf.Clamp((left == right ? 0f : right ? 1f : -1f) + x, -1f, 1f),
            forward = Mathf.Clamp((forwards == backwards ? 0f : forwards ? 1f : -1f) + y, -1f, 1f),
            yaw = yaw,
            run = Held(HoPInput.Run)
        };
    }

    private static bool Held(HoPInput action) =>
        GameSystem.settings.keySettingsLists[action].Any(binding => binding.CheckForKeyHeld());
}

/// <summary>A remote input provider must return neutral input when its sample expires.</summary>
public interface IPlayerMovementInputSource
{
    PlayerMovementInput ReadMovement(PlayerScript player);
}
