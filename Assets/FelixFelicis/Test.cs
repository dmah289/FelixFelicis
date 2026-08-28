using System;
using Horcrux.Runtime.Implementations.Utilities.Common;
using Horcrux.Runtime.Tweening.Easing;
using Horcrux.Runtime.Utilities.PhysXHelper;
using UnityEngine;

namespace FelixFelicis.FelixFelicis
{
    public class Test : MonoBehaviour
    {
        [Range(0,1)]
        [SerializeField] private float t = 1.0f;
        
        private void Update()
        {
            transform.localScale = SquashStretch.GetSquashStretch(t, EaseType.OutBack, 0.6f, AxisType.Y, CoordinateSystem.XYZ);
        }
    }
}