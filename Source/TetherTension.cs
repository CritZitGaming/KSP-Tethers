namespace KSPTethers
{
    /// <summary>
    /// The maths behind a tether's pull, on its own so it can be checked without the game running.
    /// </summary>
    internal static class TetherTension
    {
        /// <summary>Fastest the pull will ever haul the ends together to take out overstretch, in m/s.</summary>
        public const float MaxCorrectionSpeed = 4f;

        /// <summary>
        /// The pull in kN for a tether stretched <paramref name="extension"/> metres past its length whose ends
        /// are drawing apart at <paramref name="separating"/> m/s, given the inverse of the mass behind them.
        ///
        /// This is a soft constraint rather than a bare spring. When the bodies are heavy compared with the
        /// spring it comes out as exactly k*extension + (c + dt*k)*separating, the spring-damper that was
        /// asked for. When the spring is too stiff for the time step it instead stops at the impulse that
        /// just removes the separation, which is why no combination of stiffness, mass and frame rate can
        /// make it overshoot and explode. Pure maths, so it can be checked outside the game.
        /// </summary>
        public static float Solve(float extension, float separating, float invMass, float spring, float damping, float dt)
        {
            if (extension <= 0f || dt <= 0f || invMass <= 1e-9f)
                return 0f;
            float denom = dt * spring + damping;
            if (denom <= 1e-6f)
                return 0f;
            float gamma = 1f / (dt * denom);        // how much the spring's give relaxes the constraint
            float beta = dt * spring / denom;       // how much of the overstretch is taken out this step
            float effective = 1f / (invMass + gamma);
            // Pulling a big overstretch out in one step would mean yanking the ends together at any speed at
            // all, which is how a tether catapults a kerbal after a jolt or a scene load. Taking it out no
            // faster than this leaves ordinary stretches untouched - they ask for well under a metre a second
            // - and turns a huge one into a firm haul instead of a slingshot.
            float correction = beta * extension / dt;
            if (correction > MaxCorrectionSpeed)
                correction = MaxCorrectionSpeed;
            float lambda = -effective * (separating + correction);
            return lambda >= 0f ? 0f : -lambda / dt;
        }
    }
}
