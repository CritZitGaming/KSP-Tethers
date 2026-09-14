using UnityEngine;

namespace KSPTethers
{
    /// <summary>Clip / release clicks and the reel motor hum, played from the kerbal.</summary>
    internal sealed class TetherAudio
    {
        private readonly GameObject host;
        private readonly AudioSource oneShot;
        private readonly AudioSource loop;
        private readonly AudioClip clipClip;
        private readonly AudioClip releaseClip;

        public TetherAudio(Transform parent)
        {
            TetherConfig cfg = TetherConfig.Instance;
            // A child object keeps our sources away from anything that calls GetComponent<AudioSource>() on the part.
            host = new GameObject("KSPTethers-Audio");
            host.transform.SetParent(parent, false);

            oneShot = Configure(host.AddComponent<AudioSource>());
            loop = Configure(host.AddComponent<AudioSource>());
            loop.loop = true;
            loop.clip = LoadClip(cfg.reelSound);
            clipClip = LoadClip(cfg.clipSound);
            releaseClip = LoadClip(cfg.releaseSound);
        }

        private static AudioSource Configure(AudioSource s)
        {
            s.playOnAwake = false;
            s.spatialBlend = 1f;
            s.rolloffMode = AudioRolloffMode.Logarithmic;
            s.minDistance = 2f;
            s.maxDistance = 80f;
            s.dopplerLevel = 0f;
            return s;
        }

        private static AudioClip LoadClip(string url)
        {
            if (string.IsNullOrEmpty(url) || GameDatabase.Instance == null)
                return null;
            AudioClip c = GameDatabase.Instance.GetAudioClip(url);
            if (c == null)
                TetherLog.Warn("Sound not found: " + url);
            return c;
        }

        private static float Volume
        {
            get { return GameSettings.SHIP_VOLUME * TetherConfig.Instance.soundVolume; }
        }

        public void PlayClip()
        {
            if (oneShot != null && clipClip != null)
                oneShot.PlayOneShot(clipClip, Volume);
        }

        public void PlayRelease()
        {
            if (oneShot != null && releaseClip != null)
                oneShot.PlayOneShot(releaseClip, Volume * 0.8f);
        }

        public void SetReeling(bool reeling)
        {
            if (loop == null || loop.clip == null)
                return;
            if (reeling)
            {
                loop.volume = Volume * 0.35f;
                loop.pitch = 1.4f;
                if (!loop.isPlaying)
                    loop.Play();
            }
            else if (loop.isPlaying)
            {
                loop.Stop();
            }
        }

        public void Destroy()
        {
            if (host != null)
                Object.Destroy(host);
        }
    }
}
