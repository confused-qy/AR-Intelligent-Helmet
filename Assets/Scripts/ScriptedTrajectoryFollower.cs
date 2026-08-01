using UnityEngine;

/// <summary>
/// Makes a simulated vehicle follow a recorded vehicle path. Its following
/// distance changes on a repeatable timeline to demonstrate a safety warning.
/// </summary>
public sealed class ScriptedTrajectoryFollower : MonoBehaviour
{
    [Header("Path")]
    [SerializeField] private VehicleTrajectoryRecorder trajectory;
    [SerializeField] private Rigidbody vehicleRigidbody;

    [Header("Following distances (metres)")]
    [Min(0f)]
    [SerializeField] private float normalDistance = 12f;
    [Min(0f)]
    [SerializeField] private float closeDistance = 3f;

    [Header("Scripted timeline (seconds)")]
    [Tooltip("How long the car follows normally before approaching.")]
    [Min(0f)]
    [SerializeField] private float normalDuration = 8f;
    [Tooltip("How long it takes to move from the normal distance to the close distance.")]
    [Min(0.01f)]
    [SerializeField] private float approachDuration = 4f;
    [Tooltip("How long it stays close.")]
    [Min(0f)]
    [SerializeField] private float closeDuration = 4f;
    [Tooltip("How long it takes to return to the normal distance.")]
    [Min(0.01f)]
    [SerializeField] private float leaveDuration = 4f;
    [Tooltip("Extra normal-following time before the sequence repeats.")]
    [Min(0f)]
    [SerializeField] private float restDuration = 5f;
    [SerializeField] private bool repeat = true;

    [Header("Pose")]
    [Tooltip("Local offset from the recorded path. X can place the car in a neighbouring lane.")]
    [SerializeField] private Vector3 localPathOffset;
    [Tooltip("Smooths small recording steps. Use 0 to snap exactly to the path.")]
    [Min(0f)]
    [SerializeField] private float positionSmoothing = 12f;
    [Min(0f)]
    [SerializeField] private float rotationSmoothing = 12f;

    private float sequenceTime;
    private bool sequenceStarted;

    public float CurrentFollowingDistance { get; private set; }

    private void Awake()
    {
        if (vehicleRigidbody == null)
            vehicleRigidbody = GetComponent<Rigidbody>();

        CurrentFollowingDistance = normalDistance;
    }

    private void Update()
    {
        if (trajectory == null)
            return;

        // The sequence begins only after the main car has travelled far enough
        // for the follower to be placed at its normal distance.
        if (!sequenceStarted)
        {
            if (trajectory.RecordedDistance < normalDistance)
                return;

            sequenceStarted = true;
            sequenceTime = 0f;
        }

        sequenceTime += Time.deltaTime;
        CurrentFollowingDistance = EvaluateFollowingDistance(sequenceTime);
    }

    private void LateUpdate()
    {
        if (!sequenceStarted || trajectory == null)
            return;

        if (!trajectory.TryGetPoseBehind(
                CurrentFollowingDistance,
                out Vector3 pathPosition,
                out Quaternion pathRotation))
        {
            return;
        }

        Vector3 targetPosition =
            pathPosition + pathRotation * localPathOffset;
        Quaternion targetRotation = pathRotation;

        float positionT = positionSmoothing <= 0f
            ? 1f
            : 1f - Mathf.Exp(-positionSmoothing * Time.deltaTime);
        float rotationT = rotationSmoothing <= 0f
            ? 1f
            : 1f - Mathf.Exp(-rotationSmoothing * Time.deltaTime);

        Vector3 nextPosition =
            Vector3.Lerp(transform.position, targetPosition, positionT);
        Quaternion nextRotation =
            Quaternion.Slerp(transform.rotation, targetRotation, rotationT);

        // A kinematic Rigidbody keeps trigger/collision messages working.
        if (vehicleRigidbody != null && vehicleRigidbody.isKinematic)
        {
            vehicleRigidbody.position = nextPosition;
            vehicleRigidbody.rotation = nextRotation;
        }
        else
        {
            transform.SetPositionAndRotation(nextPosition, nextRotation);
        }
    }

    public void RestartSequence()
    {
        sequenceTime = 0f;
    }

    private float EvaluateFollowingDistance(float time)
    {
        float approachStart = normalDuration;
        float closeStart = approachStart + approachDuration;
        float leaveStart = closeStart + closeDuration;
        float restStart = leaveStart + leaveDuration;
        float cycleDuration = restStart + restDuration;

        if (repeat && cycleDuration > 0f)
            time %= cycleDuration;
        else
            time = Mathf.Min(time, cycleDuration);

        if (time < approachStart)
            return normalDistance;

        if (time < closeStart)
        {
            float t = Mathf.InverseLerp(approachStart, closeStart, time);
            return Mathf.Lerp(normalDistance, closeDistance, SmoothStep(t));
        }

        if (time < leaveStart)
            return closeDistance;

        if (time < restStart)
        {
            float t = Mathf.InverseLerp(leaveStart, restStart, time);
            return Mathf.Lerp(closeDistance, normalDistance, SmoothStep(t));
        }

        return normalDistance;
    }

    private static float SmoothStep(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * (3f - 2f * t);
    }
}
