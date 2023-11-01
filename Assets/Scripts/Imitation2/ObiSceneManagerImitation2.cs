using System.Collections;
using System.Collections.Generic;
using System.IO;
using System;
using System.Threading.Tasks;
using UnityEngine;
using Obi;
using Newtonsoft.Json;
using VRTRIX;

namespace ObiVR
{
    public class ObiSceneManagerImitation2 : MonoBehaviour
    {
        public GameObject solver;
        public GameObject hands;
        public string saveDir = "Captures/";
        public string assetBundleDir = "Assets/AssetBundles/";
        public string abNamesPath = "AssetBundleNames.txt";
        public string subCatePath = "Assets/AssetBundles/Tshirt_sub_categories.txt";
        public int[] subCateFilterList;
        public string type;
        public string actionTag;
        public int particleCount = 1700;
        public int maxFrameCountPerAction = 6000;
        public ObiCollisionMaterial collisionMaterial;
        public UnityEngine.UI.Text textUI;
        public ObiCollisionEventHandler2 eventHandler;
        public Collider tableCollider;
        public GameObject graspSphere;
        public Vector3 graspEndPos;
        //public GameObject leftRobotBase, rightRobotBase;
        public SkinnedMeshRenderer leftGlove, rightGlove;

        private VRTRIXGloveDataStreaming gloveData;
        private VRTRIXBoneMapping boneMapping;
        private Queue<VRTRIXGloveGesture> leftHandRecord, rightHandRecord;
        private bool isRecording = false;
        private ObiCloth actor = null;
        private GameObject clothObject = null;
        private ObiSolver obiSolver = null;
        private float inverseMassScale = 0.3f;
        private int graspSolverIndex;
        private int frameCount;
        private int currentIdx;
        private List<string> blueprintNames;
        private List<string> objNames;
        private PoseData saveData = null;
        private bool isLocked = false;
        private bool isLoading = false;
        private Material[] materials;
        private GameObject meshObject;

        public enum ColliderType
        {
            None,
            LeftHandIndexTail,
            RightHandIndexTail,
        }

        [Serializable]
        public class frameParticelInfo
        {
            public List<Vector3> positions, normals;
            public frameParticelInfo()
            {
                positions = new List<Vector3>();
                normals = new List<Vector3>();
            }
        }

        [Serializable]
        public class frameSolverIndices
        {
            public List<int> indices;
            public frameSolverIndices()
            {
                indices = new List<int>();
            }
        }

        [Serializable]
        public class frameHandPose
        {
            public Vector3[] positions, eulerAngles;
            public int leftState, rightState;
            public frameHandPose()
            {
                positions = new Vector3[(int)VRTRIXBones.NumOfBones];
                eulerAngles = new Vector3[(int)VRTRIXBones.NumOfBones];
                leftState = 0;
                rightState = 0;
            }
        }


        [Serializable]
        public class PoseData
        {
            public List<frameHandPose> dualHandPoses;
            public List<frameParticelInfo> localParticles;
            public List<frameSolverIndices> leftGraspSolverIndices, rightGraspSolverIndices;
            public List<Matrix4x4> solverLocalToWorldMatrices;
            public List<Matrix4x4> actorLocalToWorldMatrices;
            public List<float> timeStamps;
            public int[] solverIndices;
            public int maxLength;
            public float planeHeight;
            //public Vector3 leftRobotPos, rightRobotPos;
            public PoseData(int maxFrameCount)
            {
                maxLength = maxFrameCount;
                dualHandPoses = new List<frameHandPose>();
                localParticles = new List<frameParticelInfo>();
                solverIndices = null;
                leftGraspSolverIndices = new List<frameSolverIndices>();
                rightGraspSolverIndices = new List<frameSolverIndices>();
                solverLocalToWorldMatrices = new List<Matrix4x4>();
                actorLocalToWorldMatrices = new List<Matrix4x4>();
                timeStamps = new List<float>();
                planeHeight = 0.5690214f;
            }
        }

        // Start is called before the first frame update
        void Start()
        {
            if (!Directory.Exists(saveDir))
                Directory.CreateDirectory(saveDir);

            if (saveData == null)
                saveData = new PoseData(maxFrameCountPerAction);

            isRecording = false;
            isLocked = false;

            leftHandRecord = new Queue<VRTRIXGloveGesture>();
            rightHandRecord = new Queue<VRTRIXGloveGesture>();
            gloveData = hands.GetComponent<VRTRIXGloveDataStreaming>();
            boneMapping = hands.GetComponent<VRTRIXBoneMapping>();

            FindFiles();

            materials = new Material[2];

            //materials[0] = Resources.Load<Material>("ObiGreen");
            //materials[1] = Resources.Load<Material>("ObiOrange(Back)");
            //collisionMaterial = Resources.Load<ObiCollisionMaterial>("HighStaticFriction");

            var mat0 = AssetBundle.LoadFromFile(Path.Combine(assetBundleDir, "material/obigreen"));
            materials[0] = mat0.LoadAsset<Material>("obigreen");
            mat0.Unload(false);
            var mat1 = AssetBundle.LoadFromFile(Path.Combine(assetBundleDir, "material/obiorange(back)"));
            materials[1] = mat1.LoadAsset<Material>("obiorange(back)");
            mat1.Unload(false);

            //var colMat = AssetBundle.LoadFromFile(Path.Combine(assetBundleDir, "obicollisionmaterial/highstaticfriction"));
            //collisionMaterial = colMat.LoadAsset<ObiCollisionMaterial>("highstaticfriction");
            //colMat.Unload(false);

            obiSolver = solver.GetComponent<ObiSolver>();

            StartCoroutine(ConnectGloves());
            StartCoroutine(CreateActor(currentIdx));

            InvokeRepeating("Record", 1.0f, 0.04f);
        }

        void FindFiles()
        {
            var saveFiles = Directory.GetFiles(saveDir,
                $"{type}*{actionTag}*.json", SearchOption.TopDirectoryOnly);
            string maxDirNum = "%%%%%%%%";
            if (saveFiles.Length > 0)
            {
                var saveFilenames = new List<string>();
                foreach (var file in saveFiles)
                    saveFilenames.Add(file.Split('_')[3].Split('.')[0]);
                saveFilenames.Sort();
                maxDirNum = saveFilenames[saveFilenames.Count - 1];
            }

            string[] allSubCateInfo = null;
            if (File.Exists(subCatePath))
                allSubCateInfo = File.ReadAllLines(subCatePath);
            var subCateDict = new Dictionary<string, int>();
            if (subCateFilterList.Length > 0)
            {
                foreach (var line in allSubCateInfo)
                {
                    var objCode = line.Split(' ')[0];
                    var subCate = Int32.Parse(line.Split(' ')[1].Split('\n')[0]);
                    subCateDict.Add(objCode, subCate);
                }
            }

            var AllAssetBundleNames = File.ReadAllLines(abNamesPath);
            blueprintNames = new List<string>();
            objNames = new List<string>();
            foreach (var name in AllAssetBundleNames)
            {
                if (subCateFilterList.Length > 0)
                {
                    bool flag = false;
                    var objCode = name.Split('/')[0];
                    if (subCateDict.ContainsKey(objCode))
                        foreach (var subCate in subCateFilterList)
                        {
                            if (subCateDict[objCode] == subCate)
                            {
                                flag = true;
                                break;
                            }
                        }
                    if (!flag) continue;
                }

                if (name.Contains($"remesh_{particleCount}_{type.ToLower()}_blueprint"))
                    blueprintNames.Add(name);
                else if (name.Contains($"remesh_{particleCount}_{type.ToLower()}"))
                    objNames.Add(name);
            }

            Debug.Log($"Find {blueprintNames.Count} blueprints for {type} with {subCateFilterList.Length} sub-cate filters !");

            blueprintNames.Sort();
            currentIdx = -1;
            for (int i = 0; i < blueprintNames.Count; i++)
                if (blueprintNames[i].Contains(maxDirNum))
                {
                    currentIdx = i;
                    break;
                }
            // move to the next object
            currentIdx += 1;
        }

        IEnumerator ConnectGloves()
        {
            isLocked = true;
            for (int i = 0; i < 10; i++)
            {
                textUI.text = $"请五指并拢伸直双手，准备连接校准，倒计时{10 - i}秒";
                yield return new WaitForSecondsRealtime(1.0f);
            }
            gloveData.OnConnectGlove();
            StartCoroutine(ShowUI($"手套连接成功！"));
            isLocked = false;
        }

        IEnumerator CreateActor(int index)
        {
            isLoading = true;
            if (index < 0)
                index = 0;

            while (isLocked)
                yield return new WaitForEndOfFrame();

            DestoryActor();
            GC.Collect();

            // save environment info
            saveData.planeHeight = tableCollider.bounds.max.y;
            //saveData.leftRobotPos = leftRobotBase.transform.position;
            //saveData.rightRobotPos = rightRobotBase.transform.position;

            // create the cloth actor/renderer:
            clothObject = new GameObject(type, typeof(ObiCloth), typeof(ObiClothRenderer));

            // get a reference to the cloth:
            actor = clothObject.GetComponent<ObiCloth>();

            // instantiate and set the blueprint:
            //string objPath = validFilenames[index].Replace(Application.dataPath, "")
            //            .Replace("/Resources/", "").Replace("\\", "/").Replace(".asset", "");
            //var blueprint = Resources.Load<ObiClothBlueprint>(objPath);

            string blueprintPath = Path.Combine(assetBundleDir, blueprintNames[index]);
            var ab = AssetBundle.LoadFromFile(blueprintPath);
            string[] sArr = blueprintPath.Split('/');
            ObiClothBlueprint blueprint = ab.LoadAsset<ObiClothBlueprint>(sArr[sArr.Length - 1]);
            ab.Unload(false);

            actor.clothBlueprint = Instantiate(blueprint);
            //get mesh filter
            string objPath = blueprintPath.Replace("_blueprint", "");
            ab = AssetBundle.LoadFromFile(objPath);
            sArr = objPath.Split('/');
            if (meshObject != null)
                Destroy(meshObject);
            meshObject = Instantiate(ab.LoadAsset<GameObject>(sArr[sArr.Length - 1]));
            ab.Unload(false);
            actor.clothBlueprint.inputMesh = meshObject.GetComponentInChildren<MeshFilter>().mesh;

            // set collision materials
            actor.collisionMaterial = collisionMaterial;
            actor.selfCollisions = true;

            // set constraints        
            actor.aerodynamicsEnabled = true;
            actor.tetherConstraintsEnabled = false;
            actor.volumeConstraintsEnabled = false;

            actor.maxBending = 0.005f;
            actor.plasticYield = 0f;
            actor.plasticCreep = 100f;

            // set mesh renderer
            var renderer = clothObject.GetComponent<MeshRenderer>();
            renderer.materials = materials;

            // set solver position
            if (actionTag.Contains("flattening"))
            {
                solver.transform.position = new Vector3(0.095f, 1.3f, 0.35f);
                solver.transform.rotation = Quaternion.Euler(0, 0, 0);
            }

            // parent the cloth under a solver to start simulation:
            actor.transform.position = solver.transform.position;
            float eulerX, eulerY, eulerZ;
            if (actionTag == "flattening-folding-long2" || actionTag == "flattening-folding-short1")
            {
                eulerX = UnityEngine.Random.Range(-180f, 180f);
                eulerY = UnityEngine.Random.Range(-180f, 180f);
                eulerZ = UnityEngine.Random.Range(-180f, 180f);
            }
            else
            {
                bool invert = UnityEngine.Random.Range(0f, 1f) < 0.5f;
                eulerX = 0f;
                eulerY = UnityEngine.Random.Range(-180f, 180f);
                eulerZ = invert ? 180f : 0f;
            }

            if (actionTag.Contains("flattening"))
                actor.transform.rotation = Quaternion.Euler(eulerX, eulerY, eulerZ);

            actor.transform.parent = solver.transform;

            clothObject.SetActive(true);
            actor.AddToSolver();

            if (actionTag.Contains("flattening2"))
            {
                if (type == "Tshirt")
                {
                    if (actionTag.Contains("short"))
                    {
                        actor.maxBending = UnityEngine.Random.Range(0.10f, 0.8f);
                    }
                    else if (actionTag.Contains("long"))
                    {
                        actor.maxBending = UnityEngine.Random.Range(0.1f, 0.8f);
                    }
                    inverseMassScale = UnityEngine.Random.Range(0.05f, 0.2f);
                    actor.stretchingScale = UnityEngine.Random.Range(0.9f, 1.1f);
                    actor.maxCompression = 0f;
                    actor.solver.distanceConstraintParameters.iterations = 15;
                    actor.solver.bendingConstraintParameters.SORFactor = 1.0f;
                    actor.solver.bendingConstraintParameters.enabled = true;
                    actor.solver.particleCollisionConstraintParameters.evaluationOrder = Oni.ConstraintParameters.EvaluationOrder.Parallel;
                    actor.solver.particleCollisionConstraintParameters.iterations = 12;
                    //actor.solver.particleCollisionConstraintParameters.SORFactor = 0.34f;
                    //actor.solver.particleCollisionConstraintParameters.iterations = 4;
                    actor.solver.particleCollisionConstraintParameters.SORFactor = 0.8f;
                    actor.solver.particleFrictionConstraintParameters.iterations = 2;
                    actor.solver.particleFrictionConstraintParameters.SORFactor = 1.0f;
                    actor.drag = UnityEngine.Random.Range(0.005f, 0.05f);
                    actor.lift = 0f;
                }
            }
            else if (actionTag.Contains("flattening"))
            {
                actor.drag = 0.01f;
                if (type == "Tshirt")
                {
                    actor.maxBending = UnityEngine.Random.Range(0.02f, 0.05f);
                    inverseMassScale = UnityEngine.Random.Range(0.2f, 0.5f);
                    actor.solver.bendingConstraintParameters.SORFactor = 0.7f;
                    actor.solver.particleCollisionConstraintParameters.iterations = 3;
                    actor.solver.particleCollisionConstraintParameters.SORFactor = 0.34f;
                    actor.solver.particleFrictionConstraintParameters.iterations = 1;
                    actor.solver.particleFrictionConstraintParameters.SORFactor = 1.0f;
                    actor.drag = 0.02f;
                    actor.lift = 0f;
                }
            }

            // Iterate over all particles in an actor:
            for (int i = 0; i < actor.solverIndices.Length; ++i)
            {
                // retrieve the particle index in the solver:
                int solverIndex = actor.solverIndices[i];

                // change the inverse mass of this particle:
                actor.solver.invMasses[solverIndex] *= inverseMassScale;
            }

            actor.solver.parameters.gravity = new Vector3(0f, -3f, 0f);
            actor.solver.parameters.sleepThreshold = 0f;

            // reset event handler
            eventHandler.Reset();
            // wait for the garment to fall on the table
            yield return new WaitForSecondsRealtime(2f);

            // Set to -1 (null) at the beginnning
            graspSolverIndex = -1;
            if (actionTag.Contains("flattening"))
            {
                int idx = (int)(UnityEngine.Random.Range(0f, 1.0f) * actor.solverIndices.Length);
                graspSolverIndex = actor.solverIndices[idx];
                var candidatePos = actor.solver.renderablePositions[graspSolverIndex];
                // Iterate over all particles in an actor:
                for (int i = 0; i < actor.solverIndices.Length; ++i)
                {
                    // retrieve the particle index in the solver:
                    int solverIndex = actor.solverIndices[i];
                    var tmpPos = actor.solver.renderablePositions[solverIndex];
                    if (Mathf.Abs(tmpPos.x - candidatePos.x) < 0.02f && Mathf.Abs(tmpPos.z - candidatePos.z) < 0.02f
                        && Mathf.Abs(tmpPos.y - candidatePos.y) > 0.03f && tmpPos.y > candidatePos.y)
                    {
                        // find the particle with larger height (y axis)
                        graspSolverIndex = solverIndex;
                        candidatePos = actor.solver.renderablePositions[graspSolverIndex];
                        break;
                    }
                }
                StartCoroutine(UpdateGraspPosition());
            }

            StartCoroutine(ShowUI($"载入第{index}号物体成功！"));
            Debug.Log($"Creating a actor with {blueprintPath} successfully!");

            yield return new WaitForSecondsRealtime(7.5f);
            //yield return new WaitForSecondsRealtime(3.0f);
            obiSolver.parameters.gravity = new Vector3(0f, -9.81f, 0f);
            Debug.Log("Set gravity to normal!");
            StartCoroutine(ShowUI("物体随机化完成，重力恢复！"));

            isLoading = false;
        }

        void DestoryActor()
        {
            if (clothObject != null)
            {
                actor.RemoveFromSolver();
                Destroy(clothObject);
            }
            leftHandRecord.Clear();
            rightHandRecord.Clear();
        }

        void Record()
        {
            if (!isRecording && !isLocked && !isLoading && IsHandOne(false) && !IsHandOne(true))
            //if (!isRecording && !isLocked && !isLoading && Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                // Start recording if the right hand has index point state.
                isRecording = true;
                Debug.Log("Begin recording!");
                StartCoroutine(ShowUI($"开始录制{currentIdx}号数据！"));
            }
            else if (isRecording && !isLocked && !isLoading && IsHandOne(true) && !IsHandOne(false))
            //if (isRecording && !isLocked && !isLoading && Input.GetKeyDown(KeyCode.Backspace))
            {
                // Clear recent recording if the left hand has index point state.
                saveData = new PoseData(maxFrameCountPerAction);
                isRecording = false;
                frameCount = 0;
                StartCoroutine(ShowUI("已清除当前录制！\n请重新开始录制！"));
                Debug.Log("Clear recent recording!");
            }
            else if (isRecording && !isLocked && !isLoading && ((IsHandOne(true) && IsHandOne(false)) || Input.GetKeyDown(KeyCode.Space)))
            //if (isRecording && !isLocked && !isLoading && Input.GetKeyDown(KeyCode.Space))
            {
                // save data if both hands are in index point state
                string dirNum = Path.GetFileName(Path.GetDirectoryName(blueprintNames[currentIdx]));
                string time = DateTime.Now.ToLocalTime().ToString().Replace(" ", "%").Replace(":", "-").Replace("/", "-");
                string filename = $"{type}_{actionTag}_{time}_{dirNum}.json";
                string savePath = Path.Combine(saveDir, filename);
                SaveDataAsync(savePath);
                isRecording = false;
                StartCoroutine(CreateActor(++currentIdx));
            }
            //else if (!isRecording && !isLocked && !isLoading && Input.GetKeyDown(KeyCode.RightArrow))
            else if (!isRecording && !isLocked && !isLoading && ((IsHandFist(false) && !IsHandFist(true)) || Input.GetKeyDown(KeyCode.RightArrow)))
            {
                // move to the next object if right hand in fist state
                StartCoroutine(CreateActor(++currentIdx));
            }
            //else if (!isRecording && !isLocked && !isLoading && Input.GetKeyDown(KeyCode.LeftArrow))
            else if (!isRecording && !isLocked && !isLoading && ((!IsHandFist(false) && IsHandFist(true)) || Input.GetKeyDown(KeyCode.LeftArrow)))
            {
                // move to the last object if left hand in fist state
                StartCoroutine(CreateActor(--currentIdx));
            }
            else if (!isRecording && !isLocked && !isLoading)
            {
                if (textUI.text == "")
                    textUI.text = "左食指-清空录制！\n" + "右食指-开始录制！\n" + "双食指-保存数据！\n" +
                        "左握拳-上个物体！\n" + "右握拳-下个物体！";
            }

            if (isRecording && !isLocked && !isLoading)
            {
                if (textUI.text == "")
                    textUI.text = "录制中.....";
                frameCount = saveData.dualHandPoses.Count;
                if (saveData.solverIndices == null)
                    saveData.solverIndices = actor.solverIndices;

                var handPose = new frameHandPose();
                for (int boneIdx = 0; boneIdx < (int)VRTRIXBones.NumOfBones; boneIdx++)
                {
                    handPose.positions[boneIdx] = boneMapping.MyCharacterFingers[boneIdx].position;
                    handPose.eulerAngles[boneIdx] = boneMapping.MyCharacterFingers[boneIdx].eulerAngles;
                }
                handPose.leftState = (int)gloveData.GetGesture(HANDTYPE.LEFT_HAND);
                handPose.rightState = (int)gloveData.GetGesture(HANDTYPE.RIGHT_HAND);
                saveData.dualHandPoses.Add(handPose);

                var frameParticles = new frameParticelInfo();
                for (int i = 0; i < actor.solverIndices.Length; ++i)
                {
                    int solverIndex = actor.solverIndices[i];
                    //frameParticles.positions.Add(actor.solver.positions[solverIndex]);
                    frameParticles.positions.Add(actor.solver.renderablePositions[solverIndex]);
                    frameParticles.normals.Add(actor.solver.normals[solverIndex]);
                }
                saveData.localParticles.Add(frameParticles);

                saveData.solverLocalToWorldMatrices.Add(actor.solver.transform.localToWorldMatrix);
                saveData.actorLocalToWorldMatrices.Add(actor.transform.localToWorldMatrix);

                saveData.timeStamps.Add(Time.time);

                var leftSolverIndices = new frameSolverIndices();
                var rightSolverIndices = new frameSolverIndices();
                foreach (var particle_index in eventHandler.leftHeldParticles)
                    leftSolverIndices.indices.Add(particle_index);
                foreach (var particle_index in eventHandler.rightHeldParticles)
                    rightSolverIndices.indices.Add(particle_index);

                saveData.leftGraspSolverIndices.Add(leftSolverIndices);
                saveData.rightGraspSolverIndices.Add(rightSolverIndices);


                if (frameCount >= maxFrameCountPerAction)
                {
                    StartCoroutine(ShowUI("超时！\n" + "请立即保存!"));
                    Debug.Log("Exceed max frame length! Please save data now!");
                }
            }
        }

        public async Task<bool> SaveDataAsync(string path)
        {
            isLocked = true;
            StartCoroutine(ShowUI("正在保存中...."));
            await Task.Run(() =>
            {
                string jsonStr = JsonUtility.ToJson(saveData);
                File.WriteAllText(path, jsonStr);
                saveData = new PoseData(maxFrameCountPerAction);
                //GC.Collect();
            });
            isLocked = false;
            Debug.Log($"Save to {path}!");
            StartCoroutine(ShowUI("保存数据完成！"));
            return true;
        }

        private void Update()
        {
            UpdateGesture();
            UpdateGripper();
        }

        IEnumerator ShowUI(string str)
        {
            int tmpCount = 0;
            while (++tmpCount < 90)
            {
                textUI.text = str;
                yield return new WaitForEndOfFrame();
            }
            textUI.text = "";
        }

        IEnumerator UpdateGraspPosition()
        {
            obiSolver.parameters.gravity = new Vector3(0f, -3f, 0f);
            graspSphere.SetActive(true);
            var graspStartPos = solver.transform.localToWorldMatrix.
                       MultiplyPoint3x4(actor.solver.renderablePositions[graspSolverIndex]);
            while (Mathf.Abs(graspSphere.transform.position.y - graspStartPos.y) > 0.005f)
            {
                Vector3 nowPos = graspSphere.transform.position;
                var deltaPos = graspStartPos - nowPos;
                deltaPos = 0.008f / Vector3.Magnitude(deltaPos) * deltaPos;
                Vector3 nextPos = nowPos + deltaPos;
                graspSphere.transform.position = nextPos;
                yield return new WaitForFixedUpdate();
            }
            var augGraspEndPos = graspEndPos;
            if (actionTag.Contains("flattening2"))
            {
                float deltaX = UnityEngine.Random.Range(0f, 0.1f);
                float deltaY = UnityEngine.Random.Range(0f, 0.1f);
                float deltaZ = UnityEngine.Random.Range(0f, 0.05f);
                augGraspEndPos += new Vector3(deltaX, deltaY, deltaZ);
            }
            Debug.Log($"Move to point {augGraspEndPos}!");
            yield return new WaitForFixedUpdate();
            while (Mathf.Abs(graspSphere.transform.position.y - augGraspEndPos.y) > 0.005f)
            {
                Vector3 nowPos = graspSphere.transform.position;
                var deltaPos = augGraspEndPos - nowPos;
                deltaPos = 0.008f / Vector3.Magnitude(deltaPos) * deltaPos;
                Vector3 nextPos = nowPos + deltaPos;
                graspSphere.transform.position = nextPos;
                yield return new WaitForFixedUpdate();
            }
            obiSolver.parameters.gravity = new Vector3(0f, -6f, 0f);
            yield return new WaitForSecondsRealtime(2.0f);
            eventHandler.releaseGraspSphere = true;
            yield return new WaitForFixedUpdate();
            graspSphere.SetActive(false);
            graspSphere.transform.position = new Vector3(0.1f, 0.51f, 0.3f);
        }

        private void UpdateGesture()
        {
            var leftGesture = gloveData.GetGesture(HANDTYPE.LEFT_HAND);
            var rightGesture = gloveData.GetGesture(HANDTYPE.RIGHT_HAND);
            leftHandRecord.Enqueue(leftGesture);
            if (leftHandRecord.Count > 180)
                leftHandRecord.Dequeue();

            rightHandRecord.Enqueue(rightGesture);
            if (rightHandRecord.Count > 180)
                rightHandRecord.Dequeue();

            if ((leftGesture & VRTRIXGloveGesture.BUTTONPINCH) > 0)
                leftGlove.material.color = Color.yellow;
            else if ((leftGesture & VRTRIXGloveGesture.BUTTONGRAB) > 0)
                leftGlove.material.color = Color.red;
            else if ((leftGesture & VRTRIXGloveGesture.BUTTONONE) > 0)
                leftGlove.material.color = Color.green;
            else
                leftGlove.material.color = Color.white;

            if ((rightGesture & VRTRIXGloveGesture.BUTTONPINCH) > 0)
                rightGlove.material.color = Color.yellow;
            else if ((rightGesture & VRTRIXGloveGesture.BUTTONGRAB) > 0)
                rightGlove.material.color = Color.red;
            else if ((rightGesture & VRTRIXGloveGesture.BUTTONONE) > 0)
                rightGlove.material.color = Color.green;
            else
                rightGlove.material.color = Color.white;
        }

        private void UpdateGripper()
        {
            for (int handIdx = 0; handIdx < 2; handIdx++)
            {
                Transform[] thumbFingerTransforms = new Transform[4];
                Transform[] indexFingerTransforms = new Transform[4];
                if (handIdx == 0) // left hand
                {
                    thumbFingerTransforms[0] = boneMapping.MapToVRTRIX_BoneName("L_Thumb_1").transform;
                    thumbFingerTransforms[1] = boneMapping.MapToVRTRIX_BoneName("L_Thumb_2").transform;
                    thumbFingerTransforms[2] = boneMapping.MapToVRTRIX_BoneName("L_Thumb_3").transform;
                    thumbFingerTransforms[3] = boneMapping.MapToVRTRIX_BoneName("L_Thumb_3").transform.Find("finger_thumb_l_end");

                    indexFingerTransforms[0] = boneMapping.MapToVRTRIX_BoneName("L_Index_1").transform;
                    indexFingerTransforms[1] = boneMapping.MapToVRTRIX_BoneName("L_Index_2").transform;
                    indexFingerTransforms[2] = boneMapping.MapToVRTRIX_BoneName("L_Index_3").transform;
                    indexFingerTransforms[3] = boneMapping.MapToVRTRIX_BoneName("L_Index_3").transform.Find("finger_index_l_end");
                }
                else // right hand
                {
                    thumbFingerTransforms[0] = boneMapping.MapToVRTRIX_BoneName("R_Thumb_1").transform;
                    thumbFingerTransforms[1] = boneMapping.MapToVRTRIX_BoneName("R_Thumb_2").transform;
                    thumbFingerTransforms[2] = boneMapping.MapToVRTRIX_BoneName("R_Thumb_3").transform;
                    thumbFingerTransforms[3] = boneMapping.MapToVRTRIX_BoneName("R_Thumb_3").transform.Find("finger_thumb_r_end");

                    indexFingerTransforms[0] = boneMapping.MapToVRTRIX_BoneName("R_Index_1").transform;
                    indexFingerTransforms[1] = boneMapping.MapToVRTRIX_BoneName("R_Index_2").transform;
                    indexFingerTransforms[2] = boneMapping.MapToVRTRIX_BoneName("R_Index_3").transform;
                    indexFingerTransforms[3] = boneMapping.MapToVRTRIX_BoneName("R_Index_3").transform.Find("finger_index_r_end");
                }
                double[,] fMatrix = new double[5, 3];
                double[] fValue = new double[5];
                for (int i = 0; i < 5; i++)
                {
                    Transform keypointTransform;
                    // use 5 hand bones in thumb finger and index finger as keypoints
                    if (i < 2)
                        keypointTransform = thumbFingerTransforms[i + 1];
                    else
                        keypointTransform = indexFingerTransforms[i - 2];
                    // Ax + By + D = -z
                    fMatrix[i, 0] = keypointTransform.position.x;
                    fMatrix[i, 1] = keypointTransform.position.y;
                    fMatrix[i, 2] = 1;
                    fValue[i] = -keypointTransform.position.z;
                }
                int info;
                double[] coefficients;
                alglib.lsfitreport rep;
                // Fit a plane (Least Square Fitting) using multiple 3D hand keypoints
                alglib.lsfitlinear(fValue, fMatrix, out info, out coefficients, out rep);
                if (info != 1)
                    Debug.LogError("SVD decomposition failed during hand plane fitting!");
                Vector3 planeNormal = new Vector3((float)coefficients[0], (float)coefficients[1], 1.0f);
                planeNormal = planeNormal / Vector3.Magnitude(planeNormal);
                Vector3 proximateNormal = new Plane(thumbFingerTransforms[2].position,
                    indexFingerTransforms[0].position,
                    thumbFingerTransforms[1].position).normal;
                if (Vector3.Dot(planeNormal, proximateNormal) < 0)
                    // fix plane normal direction according to the proximate plane normal
                    planeNormal = -planeNormal;
                Vector3 inPoint = new Vector3(0f, 0f, -(float)coefficients[2]);
                // Construct the plane where the grigger will be in
                Plane plane = new Plane(planeNormal, inPoint);

                // find the projected point for both finger endpoints on the plane
                Vector3 thumbEndpointOnPlane = plane.ClosestPointOnPlane(thumbFingerTransforms[2].position);
                Vector3 indexEndpointOnPlane = plane.ClosestPointOnPlane(indexFingerTransforms[2].position);
                Vector3 middlePointOnPlane = (thumbEndpointOnPlane + indexEndpointOnPlane) / 2;
                Vector3 orthogonalNormal = indexEndpointOnPlane - thumbEndpointOnPlane;
                orthogonalNormal = orthogonalNormal / Vector3.Magnitude(orthogonalNormal);
                // find the orthogonal plane which has middle points on it
                Plane orthogonalPlane = new Plane(orthogonalNormal, middlePointOnPlane);
                // find the projected palm point of thumb finger
                Vector3 palmPointOnPlane = plane.ClosestPointOnPlane(orthogonalPlane.ClosestPointOnPlane(thumbFingerTransforms[0].position));

                // find the Quaternion of the gripper
                Quaternion griggerQuat = Quaternion.LookRotation(planeNormal, palmPointOnPlane - middlePointOnPlane);

            }
        }

        bool IsHandOne(bool isLeftHand)
        {
            if (isLeftHand)
            {
                foreach (var state in leftHandRecord)
                    if ((state & VRTRIXGloveGesture.BUTTONONE) > 0)
                        return true;
                return false;
            }
            else
            {
                foreach (var state in rightHandRecord)
                    if ((state & VRTRIXGloveGesture.BUTTONONE) > 0)
                        return true;
                return false;
            }
        }

        bool IsHandFist(bool isLeftHand)
        {
            if (isLeftHand)
            {
                foreach (var state in leftHandRecord)
                    if ((state & VRTRIXGloveGesture.BUTTONGRAB) > 0)
                        return true;
                return false;
            }
            else
            {
                foreach (var state in rightHandRecord)
                    if ((state & VRTRIXGloveGesture.BUTTONGRAB) > 0)
                        return true;
                return false;
            }
        }

    }

}
