using System;
using UnityEngine;

namespace Stacking
{
    /// <summary>
    /// Single owner of stacking-game state. Subscribes to the three input
    /// adapters and translates their signals into the game's three verbs:
    ///
    ///   BUILD  - the child's head leans the tower (view-only).
    ///   TILT   - the monster's hands lean the tower (view-only).
    ///   SEND   - the crane delivers a block; parent steers it; child closes
    ///            a hand to lock it.
    ///
    /// Reads from adapters, writes to StackGame + the three views.
    /// Knows nothing about faces, hands, or keypoints.
    /// </summary>
    public class StackingDirector : MonoBehaviour
    {
        public enum Phase
        {
            WaitingForChild,
            WaitingForMonster,
            WaitingForCrane,
            Building,
            GameOver,
        }

        [Header("Inputs")]
        [SerializeField] private ChildHeadInput childHead;
        [SerializeField] private BarberHandsInput monsterHands;
        [SerializeField] private ParentHandsInput craneHands;

        [Header("Gameplay targets")]
        [SerializeField] private StackGame stackGame;
        [SerializeField] private ChildBuildingView buildingView;
        [SerializeField] private BarberMonsterView monsterView;

        [Header("Rules")]
        [Tooltip("After the crane sends a block, the child has this long to lean the tower before we lock the block.")]
        [SerializeField, Min(0f)] private float autoLockSeconds = 4f;
        [Tooltip("Once the tower is leaning past this angle, the block locks.")]
        [SerializeField, Range(0f, 45f)] private float leanLockDegrees = 12f;

        public Phase CurrentPhase { get; private set; } = Phase.WaitingForChild;
        public HeadTiltSignal Head { get; private set; } = HeadTiltSignal.None;
        public MonsterHandsSignal Monster { get; private set; } = MonsterHandsSignal.None;
        public CraneSignal Crane { get; private set; } = CraneSignal.None;

        public event Action<Phase> PhaseChanged;

        private float sentAtTime;

        private void Awake()
        {
            if (stackGame == null) stackGame = FindObjectOfType<StackGame>();

            if (childHead == null) childHead = FindObjectOfType<ChildHeadInput>();
            if (monsterHands == null) monsterHands = FindObjectOfType<BarberHandsInput>();
            if (craneHands == null) craneHands = FindObjectOfType<ParentHandsInput>();

            if (stackGame != null)
                stackGame.ScoreChanged += OnScoreChanged;
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

        private void OnDestroy()
        {
            if (stackGame != null) stackGame.ScoreChanged -= OnScoreChanged;
        }

        private void Update()
        {
            if (PauseOverlay.IsPaused) return;

            // Route tilt to the building view every frame.
            if (buildingView != null) buildingView.Apply(Head);
            if (monsterView != null) monsterView.Apply(Monster);

            UpdatePhase();

            // Auto-lock: if the tower is leaning hard, or a block has been
            // moving too long, stop it.
            if (CurrentPhase == Phase.Building && stackGame != null && stackGame.IsMoving)
            {
                bool leaning = Mathf.Abs(Head.RollDegrees) > leanLockDegrees;
                bool timedOut = Time.time - sentAtTime > autoLockSeconds;

                if (timedOut)
                    stackGame.StopBlock();
                // Leaning lock is available but off by default; uncomment if wanted:
                // if (leaning) stackGame.StopBlock();
            }
        }

        private void UpdatePhase()
        {
            Phase next = CurrentPhase;

            if (stackGame == null)
            {
                next = Phase.WaitingForChild;
            }
            else if (stackGame.IsGameOver)
            {
                next = Phase.GameOver;
            }
            else if (!Head.HasFace)
            {
                next = Phase.WaitingForChild;
            }
            else if (!Monster.HasAny)
            {
                next = Phase.WaitingForMonster;
            }
            else if (!Crane.HasHand)
            {
                next = Phase.WaitingForCrane;
            }
            else
            {
                next = Phase.Building;
            }

            if (next != CurrentPhase)
            {
                CurrentPhase = next;
                PhaseChanged?.Invoke(next);
            }
        }

        private void OnHead(HeadTiltSignal signal) => Head = signal;
        private void OnMonster(MonsterHandsSignal signal) => Monster = signal;

        private void OnCrane(CraneSignal signal)
        {
            Crane = signal;
            if (stackGame == null) return;
            if (!signal.HasHand || stackGame.IsGameOver) return;

            // A raise either spawns the next block (if none moving) or steers
            // the moving one. Either way, the monster's grip is the send verb.
            if (signal.RaisedThisFrame)
            {
                if (!stackGame.IsMoving)
                {
                    stackGame.SpawnBlock();
                    sentAtTime = Time.time;
                }
            }

            if (stackGame.IsMoving)
                stackGame.SteerMovingBlock(signal.SteeredX);
        }

        private void OnScoreChanged(int _)
        {
            sentAtTime = 0f;
        }
    }
}