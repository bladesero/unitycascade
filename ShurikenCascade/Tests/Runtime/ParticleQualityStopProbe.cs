using UnityEngine;

namespace ShurikenCascade.Tests
{
    [AddComponentMenu("")]
    public sealed class ParticleQualityStopProbe : MonoBehaviour
    {
        public int StoppedCount;
        void OnParticleSystemStopped() { StoppedCount++; }
    }
}
