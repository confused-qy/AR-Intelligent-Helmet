using System;
using UnityEngine;

namespace MotorcycleNavigation
{
    public sealed class AndroidNavigationBridge : MonoBehaviour
    {
        public MotorcycleNavigationManager navigationManager;
        public bool rebuildMapFromIncomingPicture = false;

        private MotorcycleNavigationManager Manager
        {
            get
            {
                if (navigationManager == null)
                    navigationManager = FindObjectOfType<MotorcycleNavigationManager>();
                return navigationManager;
            }
        }

        public void androidMoveSend(string message)
        {
            if (Manager != null)
                Manager.androidMoveSend(message);
        }

        public void androidSend(string message)
        {
            if (Manager != null)
                Manager.androidSend(message);
        }

        public void SetGoal(string message)
        {
            if (Manager != null)
                Manager.SetGoalMessage(message);
        }

        public void AndroidSendPic(string base64)
        {
            if (!rebuildMapFromIncomingPicture || Manager == null || string.IsNullOrEmpty(base64))
                return;

            int comma = base64.IndexOf(',');
            string payload = comma >= 0 ? base64.Substring(comma + 1) : base64;
            byte[] bytes = Convert.FromBase64String(payload);
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGB24, false);
            if (texture.LoadImage(bytes))
                Manager.BuildNavigationMapFromTexture(texture);
            Destroy(texture);
        }
    }
}
