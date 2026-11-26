using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 타르코프 스타일 관성 이동 (Unity 6 / CharacterController / Input System)
/// - 가속/감속 분리, 방향 전환 시 추가 제동
/// - 공중 모멘텀 유지, 착지 페널티
/// - 스태미나, 무게에 따른 속도 감소, 앉기
/// - 경사면 추종 및 한계각 초과 시 미끄러짐
/// 시선(Yaw/Pitch) 회전은 별도 스크립트에서 처리 (이 오브젝트의 transform.rotation 기준으로 이동).
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class PlayerMotor : MonoBehaviour
{
    [Header("Speed (m/s)")]
    [SerializeField] float walkSpeed = 3.0f;
    [SerializeField] float sprintSpeed = 5.5f;
    [SerializeField] float crouchSpeed = 1.6f;
    [SerializeField, Range(0.3f, 1f)] float backwardMultiplier = 0.65f;
    [SerializeField, Range(0.3f, 1f)] float strafeMultiplier = 0.85f;

    [Header("Inertia")]
    [SerializeField] float groundAccel = 9f;          // 가속 (낮을수록 무거움)
    [SerializeField] float groundDecel = 12f;         // 입력 없을 때 감속 (낮을수록 미끄러짐)
    [SerializeField] float counterBrake = 22f;        // 반대 방향 입력 시 제동
    [SerializeField] float airAccel = 1.5f;           // 공중 조작력
    [SerializeField] float airDrag = 0f;              // 공중 감속 (0 = 모멘텀 완전 유지)

    [Header("Jump / Gravity")]
    [SerializeField] float gravity = 20f;
    [SerializeField] float jumpHeight = 0.7f;
    [SerializeField] float jumpStaminaCost = 12f;
    [SerializeField] float groundStickForce = 4f;
    [SerializeField] float landingHardSpeed = 6f;     // 이 낙하속도 이상이면 착지 페널티
    [SerializeField, Range(0f, 1f)] float landingSpeedKeep = 0.4f;
    [SerializeField] float landingRecoveryTime = 0.35f;

    [Header("Crouch")]
    [SerializeField] float standHeight = 1.8f;
    [SerializeField] float crouchHeight = 1.2f;
    [SerializeField] float stanceLerpSpeed = 10f;
    [SerializeField] Transform cameraRoot;            // 선택: 앉을 때 카메라 높이 보간
    [SerializeField] float cameraStandY = 1.65f;
    [SerializeField] float cameraCrouchY = 1.05f;

    [Header("Stamina")]
    [SerializeField] float maxStamina = 100f;
    [SerializeField] float sprintDrain = 8f;
    [SerializeField] float staminaRegen = 10f;
    [SerializeField] float regenDelay = 1.2f;
    [SerializeField] float exhaustedRecoverThreshold = 25f;

    [Header("Weight")]
    [SerializeField] float carriedWeight = 20f;       // kg (인벤토리에서 갱신)
    [SerializeField] float maxWeight = 60f;
    [SerializeField, Range(0.3f, 1f)] float minWeightSpeedMultiplier = 0.7f;

    [Header("Ground")]
    [SerializeField] LayerMask groundMask = ~0;
    [SerializeField] float groundCheckDistance = 0.15f;
    [SerializeField] float slideAccel = 18f;

    // ── 외부 노출 (카메라 bob, 발소리, 애니메이션용) ──
    public Vector3 Velocity => horizontalVelocity + Vector3.up * verticalVelocity;
    public float HorizontalSpeed => horizontalVelocity.magnitude;
    public bool IsGrounded { get; private set; }
    public bool IsSprinting { get; private set; }
    public bool IsCrouching { get; private set; }
    public float Stamina01 => stamina / maxStamina;
    public float CarriedWeight { get => carriedWeight; set => carriedWeight = Mathf.Max(0f, value); }

    CharacterController controller;
    InputAction moveAction, sprintAction, jumpAction, crouchAction;

    Vector3 horizontalVelocity;
    float verticalVelocity;
    Vector3 groundNormal = Vector3.up;
    float groundAngle;

    Vector2 moveInput;
    bool jumpQueued;
    bool wantsCrouch;
    bool sprintHeld;

    float stamina;
    float regenTimer;
    bool exhausted;
    float landingTimer;
    float lastAirVerticalSpeed;

    void Awake()
    {
        controller = GetComponent<CharacterController>();
        stamina = maxStamina;
        controller.height = standHeight;
        controller.center = new Vector3(0f, standHeight * 0.5f, 0f);

        BuildInput();
    }

    void BuildInput()
    {
        moveAction = new InputAction("Move", InputActionType.Value);
        moveAction.AddCompositeBinding("2DVector")
            .With("Up", "<Keyboard>/w")
            .With("Down", "<Keyboard>/s")
            .With("Left", "<Keyboard>/a")
            .With("Right", "<Keyboard>/d");
        moveAction.AddBinding("<Gamepad>/leftStick");

        sprintAction = new InputAction("Sprint", InputActionType.Button, "<Keyboard>/leftShift");
        sprintAction.AddBinding("<Gamepad>/leftStickPress");

        jumpAction = new InputAction("Jump", InputActionType.Button, "<Keyboard>/space");
        jumpAction.AddBinding("<Gamepad>/buttonSouth");

        crouchAction = new InputAction("Crouch", InputActionType.Button, "<Keyboard>/c");
        crouchAction.AddBinding("<Gamepad>/buttonEast");
    }

    void OnEnable()
    {
        moveAction.Enable(); sprintAction.Enable(); jumpAction.Enable(); crouchAction.Enable();
        jumpAction.performed += OnJump;
        crouchAction.performed += OnCrouchToggle;
    }

    void OnDisable()
    {
        jumpAction.performed -= OnJump;
        crouchAction.performed -= OnCrouchToggle;
        moveAction.Disable(); sprintAction.Disable(); jumpAction.Disable(); crouchAction.Disable();
    }

    void OnJump(InputAction.CallbackContext _) => jumpQueued = true;
    void OnCrouchToggle(InputAction.CallbackContext _) => wantsCrouch = !wantsCrouch;

    void Update()
    {
        float dt = Time.deltaTime;

        moveInput = Vector2.ClampMagnitude(moveAction.ReadValue<Vector2>(), 1f);
        sprintHeld = sprintAction.IsPressed();

        bool wasGrounded = IsGrounded;
        CheckGround();
        HandleLanding(wasGrounded);
        UpdateStance(dt);
        UpdateStamina(dt);
        UpdateHorizontal(dt);
        UpdateVertical(dt);

        Vector3 move = horizontalVelocity;
        // 지면 위에서는 경사면을 따라 이동 (오르막/내리막에서 튀지 않게)
        if (IsGrounded && groundAngle <= controller.slopeLimit)
            move = Vector3.ProjectOnPlane(horizontalVelocity, groundNormal);

        controller.Move((move + Vector3.up * verticalVelocity) * dt);

        // 벽에 부딪히면 해당 방향 속도 제거 (벽 비비며 관성 남는 현상 방지)
        Vector3 actual = controller.velocity; actual.y = 0f;
        if (actual.sqrMagnitude < horizontalVelocity.sqrMagnitude * 0.25f && IsGrounded)
            horizontalVelocity = Vector3.MoveTowards(horizontalVelocity, actual, 30f * dt);
    }

    // ───────────────────────── Ground ─────────────────────────
    void CheckGround()
    {
        float r = controller.radius * 0.9f;
        Vector3 bottom = transform.position + controller.center + Vector3.down * (controller.height * 0.5f - controller.radius);
        Vector3 origin = bottom + Vector3.up * 0.05f;

        bool hitGround = Physics.SphereCast(origin, r, Vector3.down, out RaycastHit hit,
            groundCheckDistance + 0.05f, groundMask, QueryTriggerInteraction.Ignore);

        IsGrounded = hitGround && verticalVelocity <= 0.01f;
        groundNormal = hitGround ? hit.normal : Vector3.up;
        groundAngle = Vector3.Angle(groundNormal, Vector3.up);
    }

    void HandleLanding(bool wasGrounded)
    {
        if (!IsGrounded) { lastAirVerticalSpeed = Mathf.Min(lastAirVerticalSpeed, verticalVelocity); return; }

        if (!wasGrounded)
        {
            float fallSpeed = -lastAirVerticalSpeed;
            if (fallSpeed > landingHardSpeed)
            {
                float t = Mathf.InverseLerp(landingHardSpeed, landingHardSpeed * 2f, fallSpeed);
                horizontalVelocity *= Mathf.Lerp(1f, landingSpeedKeep, t);
                landingTimer = landingRecoveryTime * Mathf.Lerp(0.5f, 1f, t);
            }
        }
        lastAirVerticalSpeed = 0f;
    }

    // ───────────────────────── Stance ─────────────────────────
    void UpdateStance(float dt)
    {
        // 서려는데 머리 위가 막혀있으면 계속 앉은 상태 유지
        if (!wantsCrouch && IsCrouching && !CanStandUp()) wantsCrouch = true;
        IsCrouching = wantsCrouch;

        float targetH = IsCrouching ? crouchHeight : standHeight;
        float h = Mathf.Lerp(controller.height, targetH, 1f - Mathf.Exp(-stanceLerpSpeed * dt));
        if (Mathf.Abs(h - targetH) < 0.001f) h = targetH;

        controller.height = h;
        controller.center = new Vector3(0f, h * 0.5f, 0f);

        if (cameraRoot)
        {
            Vector3 lp = cameraRoot.localPosition;
            float targetY = IsCrouching ? cameraCrouchY : cameraStandY;
            lp.y = Mathf.Lerp(lp.y, targetY, 1f - Mathf.Exp(-stanceLerpSpeed * dt));
            cameraRoot.localPosition = lp;
        }
    }

    bool CanStandUp()
    {
        float r = controller.radius * 0.95f;
        Vector3 p1 = transform.position + Vector3.up * r;
        Vector3 p2 = transform.position + Vector3.up * (standHeight - r);
        return !Physics.CheckCapsule(p1, p2, r, groundMask, QueryTriggerInteraction.Ignore);
    }

    // ───────────────────────── Stamina ─────────────────────────
    void UpdateStamina(float dt)
    {
        bool movingForward = moveInput.y > 0.1f;
        bool canSprint = sprintHeld && movingForward && !IsCrouching && IsGrounded && !exhausted && landingTimer <= 0f;
        IsSprinting = canSprint;

        if (IsSprinting)
        {
            stamina -= sprintDrain * dt;
            regenTimer = regenDelay;
            if (stamina <= 0f) { stamina = 0f; exhausted = true; IsSprinting = false; }
        }
        else
        {
            regenTimer -= dt;
            if (regenTimer <= 0f) stamina = Mathf.Min(maxStamina, stamina + staminaRegen * dt);
            if (exhausted && stamina >= exhaustedRecoverThreshold) exhausted = false;
        }
    }

    // ───────────────────────── Horizontal (관성 핵심) ─────────────────────────
    void UpdateHorizontal(float dt)
    {
        landingTimer = Mathf.Max(0f, landingTimer - dt);

        // 목표 속도 계산
        float baseSpeed = IsCrouching ? crouchSpeed : (IsSprinting ? sprintSpeed : walkSpeed);

        // 방향별 보정: 후진/횡이동은 느리게
        float dirMul = 1f;
        if (moveInput.sqrMagnitude > 0.0001f)
        {
            float back = Mathf.Clamp01(-moveInput.y);
            float side = Mathf.Abs(moveInput.x);
            dirMul = Mathf.Lerp(1f, backwardMultiplier, back);
            dirMul = Mathf.Min(dirMul, Mathf.Lerp(1f, strafeMultiplier, side));
        }

        float weightMul = Mathf.Lerp(1f, minWeightSpeedMultiplier, Mathf.Clamp01(carriedWeight / maxWeight));
        float recoverMul = landingTimer > 0f ? 0.5f : 1f;
        float exhaustMul = exhausted ? 0.8f : 1f;

        Vector3 wishDir = transform.TransformDirection(new Vector3(moveInput.x, 0f, moveInput.y));
        Vector3 targetVel = wishDir * (baseSpeed * dirMul * weightMul * recoverMul * exhaustMul);

        bool hasInput = moveInput.sqrMagnitude > 0.0001f;
        float rate;

        if (!IsGrounded)
        {
            // 공중: 모멘텀 유지, 약한 조작만 가능
            rate = hasInput ? airAccel : airDrag;
        }
        else if (groundAngle > controller.slopeLimit)
        {
            // 가파른 경사: 미끄러짐
            Vector3 slideDir = Vector3.ProjectOnPlane(Vector3.down, groundNormal).normalized;
            horizontalVelocity += slideDir * (slideAccel * dt);
            return;
        }
        else if (!hasInput)
        {
            rate = groundDecel;
        }
        else
        {
            // 현재 속도와 의도 방향이 반대에 가까울수록 더 강하게 제동
            float dot = horizontalVelocity.sqrMagnitude > 0.01f
                ? Vector3.Dot(horizontalVelocity.normalized, wishDir.normalized)
                : 1f;
            rate = Mathf.Lerp(counterBrake, groundAccel, Mathf.InverseLerp(-1f, 0.5f, dot));

            // 달리는 중 감속 입력은 더 천천히 (스프린트 관성)
            if (horizontalVelocity.magnitude > walkSpeed * 1.2f && targetVel.magnitude < horizontalVelocity.magnitude)
                rate = Mathf.Min(rate, groundDecel);
        }

        horizontalVelocity = Vector3.MoveTowards(horizontalVelocity, targetVel, rate * dt);
    }

    // ───────────────────────── Vertical ─────────────────────────
    void UpdateVertical(float dt)
    {
        if (IsGrounded)
        {
            verticalVelocity = -groundStickForce;

            bool canJump = jumpQueued && !IsCrouching && !exhausted && landingTimer <= 0f
                           && stamina >= jumpStaminaCost && groundAngle <= controller.slopeLimit;
            if (canJump)
            {
                verticalVelocity = Mathf.Sqrt(2f * gravity * jumpHeight);
                stamina -= jumpStaminaCost;
                regenTimer = regenDelay;
                IsGrounded = false;
            }
        }
        else
        {
            verticalVelocity -= gravity * dt;
            // 머리 부딪힘
            if ((controller.collisionFlags & CollisionFlags.Above) != 0 && verticalVelocity > 0f)
                verticalVelocity = 0f;
        }

        jumpQueued = false;
    }

#if UNITY_EDITOR
    void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 10, 260, 120), GUI.skin.box);
        GUILayout.Label($"Speed: {HorizontalSpeed:F2} m/s");
        GUILayout.Label($"Grounded: {IsGrounded}  Sprint: {IsSprinting}  Crouch: {IsCrouching}");
        GUILayout.Label($"Stamina: {stamina:F0}/{maxStamina:F0}  Slope: {groundAngle:F0}°");
        GUILayout.EndArea();
    }
#endif
}