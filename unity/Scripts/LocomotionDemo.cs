// 演示控制器：WASD / 方向键驱动角色在场景里走跑。
// 挂载：与 MotionBricksPlayer、MotionBricksClient、SomaHumanoidMapper 同一角色物体，
//       再把 player 拖进来（留空会自动取本物体的）。
using UnityEngine;

namespace MotionBricksBridge
{
    [RequireComponent(typeof(MotionBricksPlayer))]
    public class LocomotionDemo : MonoBehaviour
    {
        [Tooltip("留空自动取本物体的 MotionBricksPlayer")]
        public MotionBricksPlayer player;

        [Header("移动")]
        [Tooltip("走速（米/秒）")]
        public float walkSpeed = 1.5f;
        [Tooltip("按住 Shift 时的跑速（米/秒）")]
        public float runSpeed = 3.2f;
        [Tooltip("目标点放在角色前方多远（米）。越大步子越大、响应越慢。")]
        public float lookaheadDistance = 1.2f;
        [Tooltip("勾选用相机朝向解释 WASD，不勾选用角色自身朝向")]
        public bool cameraRelative = true;

        void Awake()
        {
            if (player == null)
                player = GetComponent<MotionBricksPlayer>();
        }

        void Update()
        {
            float h = Input.GetAxis("Horizontal");
            float v = Input.GetAxis("Vertical");
            bool run = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

            Vector3 input = new Vector3(h, 0f, v);
            Transform basis = (cameraRelative && Camera.main != null)
                ? Camera.main.transform
                : transform;
            Vector3 dir = basis.TransformDirection(input);
            dir.y = 0f;

            Vector3 rootPos = transform.position;
            if (dir.sqrMagnitude > 0.01f)
            {
                dir.Normalize();
                float speed = run ? runSpeed : walkSpeed;
                // 目标点：沿输入方向的前视距离处；速度快则目标放得更远。
                Vector3 target = rootPos + dir * lookaheadDistance * (speed / walkSpeed);
                player.SetLocomotionTarget(target, speed);
            }
            else
            {
                // 无输入：目标定在原地，角色减速停下（目标去重逻辑会避免重复请求）。
                player.SetLocomotionTarget(rootPos, 0f);
            }
        }
    }
}
