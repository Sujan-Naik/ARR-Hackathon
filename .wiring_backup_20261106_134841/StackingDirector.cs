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
        [Tooltip("If false, the director will NOT auto-spawn the first block. Call BeginRound() to arm it. " +
                 "Leave this ON for standalone testing of the scene without KidsSalonGameFlow; " +
                 "KidsSalonGameFlow turns it on automatically when a round starts.")]
        [SerializeField] private bool armed = false;

        [Tooltip("If the game reports not moving for this long AFTER a round is armed and a block " +
                 "has already been spawned and destroyed, spawn a replacement. Prevents the " +
                 "double-spawn you got when RestartGame briefly reports IsMoving == false.")]
        [SerializeField, Min(0f)] private float respawnGraceSeconds = 0.15f;

        [Header("Guiding detection")]
        [SerializeField, Range(0.02f, 0.5f)] private float guideEnterDistance = 0.12f;
        [SerializeField, Range(0.02f, 0.6f)] private float guideExitDistance = 0.18f;

        [Header("Tilt")]
        [SerializeField, Min(0.1f)] private float tiltLerpSpeed = 6f;

        [Header("Match")]
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

        /// <summary>
        /// Called by KidsSalonGameFlow (or any host) when a round officially starts.
        /// From that moment the director will keep a block moving. Before that it
        /// will do nothing, so no stray blocks appear before the game begins.
        /// </summary>
        public void BeginRound()
        {
            armed = true;
            _firstSpawnDone = false;
            _notMovingSince = -1f;
        }

        /// <summary>
        /// Called when a round ends or is torn down. Clears the armed flag so the
        /// next scene / next round starts clean.
        /// </summary>
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

            // Not moving. If we've never spawned, spawn immediately (this is
            // the normal "first block" path).
            if (!_firstSpawnDone)
            {
                stackGame.SpawnBlock();
                _firstSpawnDone = true;
                _notMovingSince = -1f;
                return;
            }

            // We've spawned before, and the game briefly reports not moving
            // (e.g. right after RestartGame, or during block settle). Wait a
            // grace period before spawning another, so we don't spawn a
            // duplicate during reset.
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

        private void UpdateTiltAndMatch()
        {
            if (BarberGuiding)
            {
                HeldTilt = Head.HasFace ? Head.RollDegrees : 0f;
                Match = TiltMatch.Unknown;
                MatchError01 = 0f;
            }
            else
            {
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
            if (stackGame == null || stackGame.IsGameOver) return;
            sweep?.Steer(signal.HasHand ? signal.RaisingHand : CraneSignal.Hand.None);
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