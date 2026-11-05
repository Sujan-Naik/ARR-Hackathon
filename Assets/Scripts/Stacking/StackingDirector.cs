using System;
using UnityEngine;

namespace Stacking
{
    /// <summary>
    /// Owns the stacking-game flow. Two behavioural modes:
    ///
    ///   GUIDED   - the barber's hands are near the child's head. The tower
    ///              follows the child's head. No panic.
    ///   HOLDING  - the barber's hands are away. The tower holds its last lean.
    ///              The child must mirror the held lean. Mismatch -> panic.
    ///
    /// The parent's raise toggles the block's left-to-right sweep.
    /// </summary>
    public class StackingDirector : MonoBehaviour
    {
        public enum Phase
        {
            WaitingForChild,
            WaitingForBarber,
            WaitingForParent,
            Playing,
            GameOver,
        }

        [Header("Inputs")]
        [SerializeField] private ChildHeadInput childHead;
        [SerializeField] private BarberHandsInput monsterHands;
        [SerializeField] private ParentHandsInput craneHands;

        [Header("Gameplay")]
        [SerializeField] private StackGame stackGame;
        [SerializeField] private BlockSweepController sweep;
        [SerializeField] private TiltMatchView matchView;

        [Header("Guiding detection")]
        [Tooltip("Barber hand closer than this (normalized screen distance) to the child's head means guiding.")]
        [SerializeField, Range(0.02f, 0.5f)] private float guideEnterDistance = 0.12f;
        [Tooltip("Barber hand must be farther than this to exit guiding (hysteresis).")]
        [SerializeField, Range(0.02f, 0.6f)] private float guideExitDistance = 0.18f;

        [Header("Tilt")]
        [SerializeField, Min(0.1f)] private float tiltLerpSpeed = 6f;

        [Header("Match")]
        [Tooltip("Tolerance between the child's head roll and the tower's held lean.")]
        [SerializeField, Range(1f, 30f)] private float matchToleranceDegrees = 8f;

        public Phase CurrentPhase { get; private set; } = Phase.WaitingForChild;
        public HeadTiltSignal Head { get; private set; } = HeadTiltSignal.None;
        public MonsterHandsSignal Monster { get; private set; } = MonsterHandsSignal.None;
        public CraneSignal Crane { get; private set; } = CraneSignal.None;

        public bool BarberGuiding { get; private set; }
        public float HeldTilt { get; private set; }
        public float CurrentTilt { get; private set; }
        public TiltMatch Match { get; private set; } = TiltMatch.Unknown;
        public float MatchError01 { get; private set; }

        public event Action<Phase> PhaseChanged;

        private void Awake()
        {
            if (stackGame == null) stackGame = FindObjectOfType<StackGame>();
            if (sweep == null) sweep = FindObjectOfType<BlockSweepController>();
        }

        private void OnEnable()
        {
            if (childHead != null) childHead.Changed += OnHead;
            if (monsterHands != null) monsterHands.Changed += OnMonster;
            if (craneHands != null) craneHands.Changed += OnCrane;
        }

        private void OnDisable()
        {
            if (childHead != null) childHead.Changed -= OnHead;
            if (monsterHands != null) monsterHands.Changed -= OnMonster;
            if (craneHands != null) craneHands.Changed -= OnCrane;
        }

        private void Update()
        {
            if (PauseOverlay.IsPaused) return;

            UpdateGuidingState();
            UpdateTiltAndMatch();
            ApplyTiltToTower();

            if (matchView != null)
                matchView.Apply(Match, MatchError01);

            UpdatePhase();
        }

        private void UpdateGuidingState()
        {
            if (!Head.HasFace || !Monster.HasAny)
            {
                BarberGuiding = false;
                return;
            }

            float nearest = float.MaxValue;
            if (Monster.Left.IsBound)
                nearest = Mathf.Min(nearest, Vector2.Distance(Monster.Left.Center, Head.Center));
            if (Monster.Right.IsBound)
                nearest = Mathf.Min(nearest, Vector2.Distance(Monster.Right.Center, Head.Center));

            // Hysteresis: enter at guideEnterDistance, exit at guideExitDistance.
            float threshold = BarberGuiding ? guideExitDistance : guideEnterDistance;
            BarberGuiding = nearest <= threshold;
        }

        private void UpdateTiltAndMatch()
        {
            if (BarberGuiding)
            {
                // Guided: tower follows the child's head.
                HeldTilt = Head.HasFace ? Head.RollDegrees : 0f;
                Match = TiltMatch.Unknown;
                MatchError01 = 0f;
            }
            else
            {
                // Holding: tower keeps HeldTilt; child must mirror it.
                if (!Head.HasFace)
                {
                    Match = TiltMatch.Unknown;
                    MatchError01 = 0f;
                }
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
            else if (!Crane.HasHand) next = Phase.WaitingForParent;
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
            if (!signal.HasHand || !signal.RaisedThisFrame) return;
            if (stackGame == null || stackGame.IsGameOver) return;
            sweep?.ToggleSweep();
        }
    }
}
