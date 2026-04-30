using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MF.SSGI.Demo {
    public class MFSSGI_Demo_Rotator : MonoBehaviour {

        [SerializeField] private Vector3 rotation;

        private void Update() {
            transform.rotation *= Quaternion.Euler(rotation * Time.deltaTime);
        }

    }
}