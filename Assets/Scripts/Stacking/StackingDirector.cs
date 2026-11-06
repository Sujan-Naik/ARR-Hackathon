using System;
using UnityEngine;

namespace Stacking
{
    public class StackingDirector : MonoBehaviour
    {
        public enum Phase { WaitingForChild, WaitingForBarber, WaitingForParent, Playing, GameOver }

        [Header("Inputs")]
        [SerializeField] private ChildHeadInput childHead;
        [SerializeField] private BarberHandsInput monsterHands;
        [SerializeField] private ParentHandsInput craneHands;
        [SerializeField] private ChildHandsInput childHands;

        [Header("Gameplay")]
        [SerializeField] private StackGame stackGame;
        [SerializeField] private BlockSweepController sweep;
        [SerializeField] private TiltMatchView matchView;

        [Header("Round gating")]
        [SerializeField] private bool armed = false;
        [SerializeField, Min(0f)] private float respawnGraceSeconds = 0.15f;

        [Header("Guiding detection")]
        [SerializeField, Range(0.02f, 0.5f)] private float guideEnterDistance = 0.12f;
        [SerializeField, Range(0.02f, 0.6f)] private float guideExitDistance = 0.18f;

        [Header("Tilt")]
        [SerializeField, Min(0.1f)] private float tiltLerpSpeed = 6f;

        [Header("Match")]
        [SerializeField, Range(1f, 30f)] private float matchToleranceDegrees = 8f;

        [Header("Barber hand tilt")]
        [Tooltip("When the barber's hands are visible, they rotate the tower " +
                 "based on their horizontal offset from the barber's face. " +
                 "When hands are not visible, the tower springs back to upright.")]
        [SerializeField] private bool barberHandsTiltTower = true;

        [Tooltip("Peak tower roll (degrees) when a barber hand is fully " +
                 "offset to one side of the barber's face.")]
        [SerializeField, Range(0f, 90f)] private float barberTiltMaxDegrees = 35f;

        [Tooltip("Normalized horizontal offset (from the barber's face centre) " +
                 "that maps to the full barberTiltMaxDegrees. Larger = less " +
                 "sensitive.")]
        [SerializeField, Range(0.02f, 0.5f)] private float barberTiltFullOffset = 0.15f;

        [Tooltip("When true, uses the average of both barber hands' offsets. " +
                 "When false, uses whichever hand is further from the face " +
                 "centre.")]
        [SerializeField] private bool barberTiltUseAverage = true;

        public Phase CurrentPhase { get; private set; } = Phase.WaitingForChild;
        public HeadTiltSignal Head { get; private set; } = HeadTiltSignal.None;
        public MonsterHandsSignal Monster { get; private set; } = MonsterHandsSignal.None;
        public CraneSignal Crane { get; private set; } = CraneSignal.None;

        public bool BarberGuiding { get; private set; }
        public bool BarberHandsVisible { get; private set; }
        public float BarberTiltTarget { get; private set; }
        public float HeldTilt { get; private set; }
        public float CurrentTilt { get; private set; }
        public TiltMatch Match { get; private set; } = TiltMatch.Unknown;
        public float MatchError01 { get; private set; }

        public bool ParentPresent => Crane.HasHand;
        public CraneSignal.Hand ParentRaisingHand => Crane.RaisingHand;
        public bool IsArmed => armed;

        public event Action<Phase> PhaseChanged;

        private bool _firstSpawnDone;
        private float _notMovingSince = -1f;

        private void Awake()
        {
            if (stackGame == null) stackGame = FindObjectOfType<StackGame>();
            if (sweep == null) sweep = FindObjectOfType<BlockSweepController>();
            if (childHands == null) childHands = FindObjectOfType<ChildHandsInput>();
        }

        private void OnEnable()
        {
            if (childHead != null) childHead.Changed += OnHead;
            if (monsterHands != null) monsterHands.Changed += OnMonster;
            if (craneHands != null) craneHands.Changed += OnCrane;
            if (childHands != null) childHands.Placed += OnChildPlaced;
        }

        private void OnDisable()
        {
            if (childHead != null) childHead.Changed -= OnHead;
            if (monsterHands != null) monsterHands.Changed -= OnMonster;
            if (craneHands != null) craneHands.Changed -= OnCrane;
            if (childHands != null) childHands.Placed -= OnChildPlaced;
        }

        public void BeginRound()
        {
            armed = true;
            _firstSpawnDone = false;
            _notMovingSince = -1f;
        }

        public void EndRound()
        {
            armed = false;
            _firstSpawnDone = false;
            _notMovingSince = -1f;
        }

        private void Update()
        {
            if (PauseOverlay.IsPaused) return;

            EnsureFirstBlockSpawned();
            UpdateGuidingState();
            UpdateBarberTilt();
            UpdateTiltAndMatch();
            ApplyTiltToTower();

            if (matchView != null) matchView.Apply(Match, MatchError01);
            UpdatePhase();
        }

        private void EnsureFirstBlockSpawned()
        {
            if (!armed) return;
            if (stackGame == null || stackGame.IsGameOver) return;

            if (stackGame.IsMoving)
            {
                _firstSpawnDone = true;
                _notMovingSince = -1f;
                return;
            }

            if (!_firstSpawnDone)
            {
                stackGame.SpawnBlock();
                _firstSpawnDone = true;
                _notMovingSince = -1f;
                return;
            }

            if (_notMovingSince < 0f)
                _notMovingSince = Time.unscaledTime;

            if (Time.unscaledTime - _notMovingSince < respawnGraceSeconds)
                return;

            stackGame.SpawnBlock();
            _notMovingSince = -1f;
        }

        private void UpdateGuidingState()
        {
            if (!Head.HasFace || !Monster.HasAny) { BarberGuiding = false; return; }

            float nearest = float.MaxValue;
            if (Monster.Left.IsBound) nearest = Mathf.Min(nearest, Vector2.Distance(Monster.Left.Center, Head.Center));
            if (Monster.Right.IsBound) nearest = Mathf.Min(nearest, Vector2.Distance(Monster.Right.Center, Head.Center));

            float threshold = BarberGuiding ? guideExitDistance : guideEnterDistance;
            BarberGuiding = nearest <= threshold;
        }

        /// <summary>
        /// Whenever the barber's hands are visible, compute a target roll
        /// from their horizontal offset relative to the barber's face centre
        /// (and the child's head centre if available). Hands to the left of
        /// the face tilt the tower one way, hands to the right the other.
        /// When no hands are visible, the target is upright (0 degrees).
        /// </summary>
        private void UpdateBarberTilt()
        {
            if (!barberHandsTiltTower) { BarberHandsVisible = false; BarberTiltTarget = 0f; return; }

            if (!Monster.HasAny)
            {
                BarberHandsVisible = false;
                BarberTiltTarget = 0f;
                return;
            }

            BarberHandsVisible = true;

            // Reference: the barber's face centre if we have it, otherwise
            // the child's head centre, otherwise screen centre.
            Vector2 reference;
            if (Monster.ReferenceFaceCentre.HasValue)
                reference = Monster.ReferenceFaceCentre.Value;
            else if (Head.HasFace)
                reference = Head.Center;
            else
                reference = new Vector2(0.5f, 0.5f);

            float offsetSum = 0f;
            int offsetCount = 0;
            float maxAbsOffset = 0f;

            if (Monster.Left.IsBound)
            {
                float dx = Monster.Left.Center.x - reference.x;
                offsetSum += dx;
                offsetCount++;
                if (Mathf.Abs(dx) > maxAbsOffset) maxAbsOffset = Mathf.Abs(dx);
            }
            if (Monster.Right.IsBound)
            {
                float dx = Monster.Right.Center.x - reference.x;
                offsetSum += dx;
                offsetCount++;
                if (Mathf.Abs(dx) > maxAbsOffset) maxAbsOffset = Mathf.Abs(dx);
            }

            if (offsetCount == 0) { BarberTiltTarget = 0f; return; }

            float offset = barberTiltUseAverage
                ? offsetSum / offsetCount
                : Mathf.Sign(offsetSum) * maxAbsOffset;

            float full = Mathf.Max(0.02f, barberTiltFullOffset);
            float normalized = Mathf.Clamp(offset / full, -1f, 1f);

            // Convention: hand to screen-RIGHT of the barber face => tower
            // tilts clockwise (negative Z in Unity) so it visually "leans
            // away" from the hand. Flip the sign if you want the opposite.
            BarberTiltTarget = -normalized * barberTiltMaxDegrees;
        }

        private void UpdateTiltAndMatch()
        {
            // Barber hands visible: they own the tilt. The child's head roll
            // no longer holds the pose in this mode.
            if (BarberHandsVisible)
            {
                HeldTilt = BarberTiltTarget;
                Match = TiltMatch.Unknown;
                MatchError01 = 0f;
            }
            else
            {
                // No barber hands: spring back to upright. (Child head roll
                // no longer sets HeldTilt; that was the previous behaviour
                // where the child held the pose after the barber released.)
                HeldTilt = 0f;

                if (!Head.HasFace) { Match = TiltMatch.Unknown; MatchError01 = 0f; }
                else
                {
                    float error = Mathf.Abs(Head.RollDegrees - HeldTilt);
                    MatchError01 = Mathf.Clamp01(error / Mathf.Max(1f, matchToleranceDegrees));
                    Match = error <= matchToleranceDegrees ? TiltMatch.Matched : TiltMatch.Mismatched;
                }
            }

            CurrentTilt = Mathf.Lerp(CurrentTilt, HeldTilt, Time.deltaTime * tiltLerpSpeed);
        }

        private void ApplyTiltToTower()
        {
            if (stackGame == null || stackGame.TowerRoot == null) return;
            stackGame.TowerRoot.localRotation = Quaternion.Euler(0f, 0f, CurrentTilt);
        }

        private void UpdatePhase()
        {
            Phase next;
            if (stackGame == null) next = Phase.WaitingForChild;
            else if (stackGame.IsGameOver) next = Phase.GameOver;
            else if (!Head.HasFace) next = Phase.WaitingForChild;
            else if (!Monster.HasAny) next = Phase.WaitingForBarber;
            else next = Phase.Playing;
            if (next == CurrentPhase) return;
            CurrentPhase = next;
            PhaseChanged?.Invoke(next);
        }

        private void OnHead(HeadTiltSignal s) => Head = s;
        private void OnMonster(MonsterHandsSignal s) => Monster = s;

        private void OnCrane(CraneSignal signal)
        {
            Crane = signal;
            if (stackGame == null || stackGame.IsGameOver) return;
            if (sweep == null) return;
            if (signal.HasHand && signal.RaisingHand != CraneSignal.Hand.None) sweep.Steer(signal.RaisingHand);
            else if (!signal.HasHand) sweep.ReleaseSteer();
        }

        private void OnChildPlaced()
        {
            if (stackGame == null || stackGame.IsGameOver) return;
            if (stackGame.IsMoving)
                stackGame.StopBlock();
            if (!stackGame.IsMoving)
                stackGame.SpawnBlock();
        }
    }
}