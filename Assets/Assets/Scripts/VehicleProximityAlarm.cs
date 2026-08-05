using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Plays a warning while two vehicles are too close. Separate start and stop
/// distances prevent the alarm from rapidly toggling at the threshold.
/// </summary>
public sealed class VehicleProximityAlarm : MonoBehaviour
{
    [Header("Vehicles")]
    [SerializeField] private Transform playerVehicle;
    [SerializeField] private Transform simulatedVehicle;
    [Tooltip("Optional. Assign both colliders to measure surface-to-surface distance.")]
    [SerializeField] private Collider playerCollider;
    [SerializeField] private Collider simulatedVehicleCollider;

    [Header("Warning")]
    [SerializeField] private AudioSource warningAudio;
    [Min(0f)]
    [SerializeField] private float startWarningDistance = 4f;
    [Min(0f)]
    [SerializeField] private float stopWarningDistance = 5f;
    [SerializeField] private bool forceAudioLoop = true;

    [Header("Optional events")]
    [SerializeField] private UnityEvent onWarningStarted;
    [SerializeField] private UnityEvent onWarningStopped;

    public bool IsWarningActive { get; private set; }
    public float CurrentDistance { get; private set; }

    private void Awake()
    {
        if (simulatedVehicle == null)
            simulatedVehicle = transform;

        if (warningAudio == null)
            warningAudio = GetComponent<AudioSource>();

        if (warningAudio != null && forceAudioLoop)
            warningAudio.loop = true;

        if (stopWarningDistance < startWarningDistance)
            stopWarningDistance = startWarningDistance;
    }

    private void Update()
    {
        if (playerVehicle == null || simulatedVehicle == null)
            return;

        CurrentDistance = MeasureDistance();

        if (!IsWarningActive &&
            CurrentDistance <= startWarningDistance)
        {
            StartWarning();
        }
        else if (IsWarningActive &&
                 CurrentDistance >= stopWarningDistance)
        {
            StopWarning();
        }
    }

    private void OnDisable()
    {
        StopWarning();
    }

    private float MeasureDistance()
    {
        if (playerCollider != null && simulatedVehicleCollider != null)
        {
            Vector3 pointOnPlayer =
                playerCollider.ClosestPoint(simulatedVehicleCollider.bounds.center);
            Vector3 pointOnSimulated =
                simulatedVehicleCollider.ClosestPoint(pointOnPlayer);
            return Vector3.Distance(pointOnPlayer, pointOnSimulated);
        }

        return Vector3.Distance(
            playerVehicle.position,
            simulatedVehicle.position);
    }

    private void StartWarning()
    {
        IsWarningActive = true;

        if (warningAudio != null && !warningAudio.isPlaying)
            warningAudio.Play();

        onWarningStarted?.Invoke();
    }

    private void StopWarning()
    {
        if (!IsWarningActive)
            return;

        IsWarningActive = false;

        if (warningAudio != null)
            warningAudio.Stop();

        onWarningStopped?.Invoke();
    }
}
