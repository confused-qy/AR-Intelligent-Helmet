using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Records a vehicle's travelled path so another vehicle can follow the same
/// path at a configurable distance behind it.
/// </summary>
public sealed class VehicleTrajectoryRecorder : MonoBehaviour
{
    [Header("Vehicle to record")]
    [SerializeField] private Transform target;

    [Header("Recording")]
    [Min(0.02f)]
    [SerializeField] private float sampleSpacing = 0.15f;
    [Min(10f)]
    [SerializeField] private float historyLength = 100f;

    private struct PathSample
    {
        public Vector3 position;
        public Quaternion rotation;
        public float travelledDistance;
    }

    private readonly List<PathSample> samples = new List<PathSample>();
    private float totalTravelledDistance;

    public float RecordedDistance
    {
        get
        {
            if (samples.Count < 2)
                return 0f;

            return samples[samples.Count - 1].travelledDistance -
                   samples[0].travelledDistance;
        }
    }

    private void Awake()
    {
        if (target == null)
            target = transform;

        ResetHistory();
    }

    private void LateUpdate()
    {
        RecordCurrentPose();
    }

    public void ResetHistory()
    {
        samples.Clear();
        totalTravelledDistance = 0f;

        if (target != null)
            AddSample(target.position, target.rotation);
    }

    /// <summary>
    /// Gets a pose on the recorded path by travelled distance behind the
    /// current vehicle position. Returns false until enough path is recorded.
    /// </summary>
    public bool TryGetPoseBehind(
        float distanceBehind,
        out Vector3 position,
        out Quaternion rotation)
    {
        position = default;
        rotation = Quaternion.identity;

        if (samples.Count < 2)
            return false;

        float wantedDistance =
            samples[samples.Count - 1].travelledDistance -
            Mathf.Max(0f, distanceBehind);

        if (wantedDistance < samples[0].travelledDistance)
            return false;

        int low = 0;
        int high = samples.Count - 1;

        while (low + 1 < high)
        {
            int middle = (low + high) / 2;
            if (samples[middle].travelledDistance < wantedDistance)
                low = middle;
            else
                high = middle;
        }

        PathSample from = samples[low];
        PathSample to = samples[high];
        float segmentLength = to.travelledDistance - from.travelledDistance;
        float t = segmentLength > 0.0001f
            ? (wantedDistance - from.travelledDistance) / segmentLength
            : 0f;

        position = Vector3.Lerp(from.position, to.position, t);
        rotation = Quaternion.Slerp(from.rotation, to.rotation, t);
        return true;
    }

    private void RecordCurrentPose()
    {
        if (target == null)
            return;

        if (samples.Count == 0)
        {
            AddSample(target.position, target.rotation);
            return;
        }

        PathSample last = samples[samples.Count - 1];
        float moved = Vector3.Distance(last.position, target.position);
        if (moved < sampleSpacing)
            return;

        totalTravelledDistance += moved;
        AddSample(target.position, target.rotation);
        TrimOldSamples();
    }

    private void AddSample(Vector3 position, Quaternion rotation)
    {
        samples.Add(new PathSample
        {
            position = position,
            rotation = rotation,
            travelledDistance = totalTravelledDistance
        });
    }

    private void TrimOldSamples()
    {
        float oldestAllowed = totalTravelledDistance - historyLength;
        int removeCount = 0;

        // Keep one sample before the cutoff for smooth interpolation.
        while (removeCount + 1 < samples.Count &&
               samples[removeCount + 1].travelledDistance < oldestAllowed)
        {
            removeCount++;
        }

        if (removeCount > 0)
            samples.RemoveRange(0, removeCount);
    }
}
