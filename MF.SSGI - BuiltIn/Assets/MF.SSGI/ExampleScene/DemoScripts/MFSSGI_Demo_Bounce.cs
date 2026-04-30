using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MF.SSGI.Demo {
    public class MFSSGI_Demo_Bounce : MonoBehaviour {

        [SerializeField] private Vector3 axis = Vector3.up;
        [SerializeField] private float speed = 1f;
        [SerializeField] private float magnitude = 5f;

        private Vector3 startPos;

        private void Start() {
            startPos = transform.position;
        }

        private void Update() {
            transform.position = startPos + (axis * (Mathf.Sin(Mathf.PI * speed * Time.realtimeSinceStartup) * magnitude));
        }

    }
}