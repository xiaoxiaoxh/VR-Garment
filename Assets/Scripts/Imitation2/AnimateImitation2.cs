using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json.Bson;
using Newtonsoft.Json;
using System.Threading.Tasks;
using VRTRIX;
using Obi;

namespace ObiVR
{
    public class AnimateImitation2 : MonoBehaviour
    {
        public string loadJsonDir = "Captures/";
        public string assetBundleDir = "Assets/AssetBundles/";
        public string normalCameraIntrinsicPath = "Assets/Resources/camera_intrinsic_640.json";
        public string type;
        public string actionTag;
        public int actionSteps = 2;
        public int particleCount;
        public bool isDebug = false;
        public bool showHands = false;
        public bool randomShow = false;

        public VRTRIXBoneMapping handsBoneMapping;
        public HandBones handsEndBones;
        public GameObject plane;
        public ImageSynthesis[] synths;
        public string saveDir = "Records/";
        public string dataPrefix;
        public bool grayscale = false;
        public bool save = false;
        public bool saveImgToBson = false;
        public float focal = 8f;
        public int seed = 0;

        public float minCameraDist = 1.4f;
        public float maxCameraDist = 1.6f;
        public float minPitchAngle = 40f;
        public float maxPitchAngle = 70f;

        private ObiTriangleSkinMap skinMap;
        private ObiClothBlueprint blueprint;
        private Mesh slaveMesh;

        private Vector3[] slavePositions;
        private Vector3[] slaveNormals;
        private Vector4[] slaveTangents;

        private MeshFilter meshFilter;
        private MeshRenderer meshRenderer;

        private int width;
        private int height;
        private GameObject obj = null;
        private int frameCount = 0;
        private int particleCountNow;
        private int currentIdx = -1;
        private List<string> jsonFilenames;
        private bool isRunning = false;
        private Vector3 objCenter;
        private ObiSceneManagerImitation2.PoseData loadData = null;
        private Material[] materials;
        private List<GameObject> particles;
        private AnimationData saveAnimationData;
        private ImageData saveImageData;
        private bool isSaving = false;
        private string saveAnimationBsonPath;
        private string saveImageBsonPath;
        private float lastPlaneHeight = 0.5690214f;

        public enum ActionType
        {
            Fling = 0,
            Drag = 1,
            Fold = 2,
            PickAndPlace = 3
        }

        [Serializable]
        public class CameraIntrinsic
        {
            public int width, height;
            public List<int> intrinsic_matrix;
        }

        [Serializable]
        public class AnimationData
        {
            public List<float[,]> vertices;
            public float[,] static_vertices;
            public int[] static_triangles;
            public List<List<int>> leftGraspIndices, rightGraspIndices;
            public List<float[,]> leftHandPositions, rightHandPositions;
            public List<float[,]> leftHandEulerAngles, rightHandEulerAngles;
            public List<int> leftHandStates, rightHandStates;
            public List<float[,]> cameraLocalToWorldMatrices;
            public AnimationData(int vertexCount, int triCount)
            {
                vertices = new List<float[,]>();
                static_vertices = new float[vertexCount, 3];
                static_triangles = new int[triCount];
                leftGraspIndices = new List<List<int>>();
                rightGraspIndices = new List<List<int>>();
                leftHandPositions = new List<float[,]>();
                rightHandPositions = new List<float[,]>();
                leftHandEulerAngles = new List<float[,]>();
                rightHandEulerAngles = new List<float[,]>();
                leftHandStates = new List<int>();
                rightHandStates = new List<int>();
                cameraLocalToWorldMatrices = new List<float[,]>();
            }
        }
        [Serializable]
        public class ImageData
        {
            public List<string[]> color_imgs;
            public List<string[]> depth_imgs;
            public List<string[]> mask_imgs;
            public List<string> img_names;
            public ImageData()
            {
                color_imgs = new List<string[]>();
                depth_imgs = new List<string[]>();
                mask_imgs = new List<string[]>();
                img_names = new List<string>();
            }
        }
        // Start is called before the first frame update
        void Start()
        {
            UnityEngine.Random.InitState(seed);
            jsonFilenames = new List<string>();
            var filenames = Directory.GetFiles(loadJsonDir, $"{type}*{actionTag}*.json", SearchOption.TopDirectoryOnly);
            foreach (var filename in filenames)
                if (filename.Contains(type) && filename.Contains(actionTag))
                    jsonFilenames.Add(filename);
            jsonFilenames.Sort();
            frameCount = 0;

            if (!randomShow)
            {
                string bsonDir = Path.Combine(saveDir, dataPrefix, "animation");
                if (File.Exists(bsonDir))
                {
                    var savedBsonNames = Directory.GetFiles(bsonDir, $"{type}*{actionTag}*.bson", SearchOption.TopDirectoryOnly);
                    Debug.Log($"Found {savedBsonNames.Length} .bson files!");
                    currentIdx += savedBsonNames.Length;
                }
            }

            materials = new Material[2];
            var mat0 = AssetBundle.LoadFromFile(Path.Combine(assetBundleDir, "material/obigreen"));
            materials[0] = mat0.LoadAsset<Material>("obigreen");
            mat0.Unload(false);
            var mat1 = AssetBundle.LoadFromFile(Path.Combine(assetBundleDir, "material/obiorange(back)"));
            materials[1] = mat1.LoadAsset<Material>("obiorange(back)");
            mat1.Unload(false);

            particles = new List<GameObject>();

            handsBoneMapping.gameObject.SetActive(showHands);

            SetupNormalCamera();
            foreach (var synth in synths)
                synth.grayscale = grayscale;
        }

        void SetupNormalCamera()
        {
            // setup camera parameters        
            string json_filename = normalCameraIntrinsicPath;
            if (!File.Exists(json_filename))
            {
                Debug.LogError($"{json_filename} does not exist!");
            }
            StreamReader sr = new StreamReader(json_filename);
            string jsonStr = sr.ReadToEnd();
            CameraIntrinsic intrinsic = JsonUtility.FromJson<CameraIntrinsic>(jsonStr);
            var intrinsic_matrix = intrinsic.intrinsic_matrix;

            float ax, ay, sizeX, sizeY;
            float x0, y0, shiftX, shiftY;
            ax = (float)intrinsic_matrix[0];
            ay = (float)intrinsic_matrix[4];
            x0 = (float)intrinsic_matrix[6];
            y0 = (float)intrinsic_matrix[7];

            width = (int)intrinsic.width;
            height = (int)intrinsic.height;

            sizeX = focal * width / ax;
            sizeY = focal * height / ay;

            shiftX = -(x0 - width / 2.0f) / width;
            shiftY = (y0 - height / 2.0f) / height;

            if (synths.Length != 4)
                Debug.LogError("The number of cameras must be 4!!!!");
            foreach (var synth in synths)
            {
                var mainCamera = synth.gameObject;
                mainCamera.GetComponent<Camera>().usePhysicalProperties = true;
                mainCamera.GetComponent<Camera>().sensorSize = new Vector2(sizeX, sizeY);     // in mm, mx = 1000/x, my = 1000/y
                mainCamera.GetComponent<Camera>().focalLength = focal;                            // in mm, ax = f * mx, ay = f * my
                mainCamera.GetComponent<Camera>().lensShift = new Vector2(shiftX, shiftY);    // W/2,H/w for (0,0), 1.0 shift in full W/H in image plane
            }

        }

        //void UpdateCamera()
        //{
        //    foreach (var synth in synths)
        //    {
        //        var mainCamera = synth.gameObject;
        //        Vector3 dualRobotCenter = (loadData.leftRobotPos + loadData.rightRobotPos) / 2;
        //        dualRobotCenter.y = loadData.planeHeight;
        //        mainCamera.transform.position = dualRobotCenter + cameraLocalPos;
        //    }
        //}

        void Update()
        {
            if (!isSaving)
            {
                if (!isRunning)
                {
                    if (saveAnimationData != null && saveAnimationBsonPath != null)
                        SaveAllData();

                    frameCount = 0;
                    UpdateActor();
                    UpdateCamera();
                    foreach (var synth in synths)
                        synth.OnSceneChange(grayscale);
                    AddStaticData();
                    isRunning = true;
                    return;
                }
                else
                {
                    UpdateSkinning();
                    UpdateHands();
                    if (isDebug)
                        UpdateParticles();
                }

                if (save && isRunning)
                {
                    // save images
                    string loadJsonPath = jsonFilenames[currentIdx];
                    string imgPath = Path.Combine(saveDir, dataPrefix);
                    string imgDir = Path.GetFileNameWithoutExtension(loadJsonPath);
                    string imgFilename = frameCount.ToString().PadLeft(4, '0');

                    Action<string, string> saveCallBack = null;
                    if (saveImgToBson)
                    {
                        int num_cameras = synths.Length;
                        saveImageData.color_imgs.Add(new string[num_cameras]);
                        saveImageData.depth_imgs.Add(new string[num_cameras]);
                        saveImageData.mask_imgs.Add(new string[num_cameras]);
                        saveImageData.img_names.Add(imgFilename);
                        saveCallBack = AddImageData;
                    }
                    for (int i = 0; i < synths.Length; i++)
                    {
                        var synth = synths[i];
                        string viewDir = $"view{i}";
                        synth.Save(imgFilename, width, height, Path.Combine(imgPath, "color", imgDir, viewDir), 0, saveCallBack);
                        synth.Save(imgFilename, width, height, Path.Combine(imgPath, "mask", imgDir, viewDir), 2, saveCallBack);
                        synth.Save(imgFilename, width, height, Path.Combine(imgPath, "depth", imgDir, viewDir), 3, saveCallBack);
                        //synth.Save(imgFilename, width, height, Path.Combine(imgPath, "nocs", imgDir, viewDir), 6, saveCallBack);
                    }
                    AddDynamicData();
                }

                frameCount++;
                if (frameCount >= loadData.localParticles.Count)
                    isRunning = false;
            }

        }

        void AddImageData(string filename, string img_str)
        {
            int view_idx = 0;
            if (filename.Contains("view0"))
                view_idx = 0;
            else if (filename.Contains("view1"))
                view_idx = 1;
            else if (filename.Contains("view2"))
                view_idx = 2;
            else if (filename.Contains("view3"))
                view_idx = 3;

            int img_num = saveImageData.img_names.Count;
            if (filename.Contains("color"))
                saveImageData.color_imgs[img_num - 1][view_idx] = img_str;
            else if (filename.Contains("mask"))
                saveImageData.mask_imgs[img_num - 1][view_idx] = img_str;
            else if (filename.Contains("depth"))
                saveImageData.depth_imgs[img_num - 1][view_idx] = img_str;
        }

        void AddStaticData()
        {
            foreach (var synth in synths)
            {
                var matrix = synth.gameObject.transform.localToWorldMatrix;
                float[,] cameraLocalToWorldMatrix = new float[4, 4] {
                        //{-matrix[0, 0], matrix[0, 1], matrix[0, 2], matrix[0, 3] },
                        //{matrix[1, 0], -matrix[1, 1], -matrix[1, 2], -matrix[1, 3] },
                        //{-matrix[2, 0], matrix[2, 1], matrix[2, 2], matrix[2, 3] },
                        //{-matrix[3, 0], matrix[3, 1], matrix[3, 2], matrix[3, 3] } 
                        {matrix[0, 0], matrix[0, 1], matrix[0, 2], matrix[0, 3] },
                        {matrix[1, 0], matrix[1, 1], matrix[1, 2], matrix[1, 3] },
                        {matrix[2, 0], matrix[2, 1], matrix[2, 2], matrix[2, 3] },
                        {matrix[3, 0], matrix[3, 1], matrix[3, 2], matrix[3, 3] }
                    };
                saveAnimationData.cameraLocalToWorldMatrices.Add(cameraLocalToWorldMatrix);
            }

            var vertices = slaveMesh.vertices;
            var triangles = slaveMesh.triangles;
            int vertexCount = vertices.Length;
            for (int i = 0; i < vertexCount; i++)
            {
                saveAnimationData.static_vertices[i, 0] = vertices[i].x;
                saveAnimationData.static_vertices[i, 1] = vertices[i].y;
                saveAnimationData.static_vertices[i, 2] = vertices[i].z;
            }
            int triCount = triangles.Length;
            for (int i = 0; i < triCount; i++)
                saveAnimationData.static_triangles[i] = triangles[i];
        }


        void AddDynamicData()
        {
            var vertices = slaveMesh.vertices;
            int vertexCount = vertices.Length;
            var frameData = new float[vertexCount, 3];
            for (int i = 0; i < vertexCount; i++)
            {
                frameData[i, 0] = vertices[i].x;
                frameData[i, 1] = vertices[i].y;
                frameData[i, 2] = vertices[i].z;
            }
            saveAnimationData.vertices.Add(frameData);

            Matrix4x4 localToWorld = loadData.solverLocalToWorldMatrices[frameCount];
            Vector3[] particleWorldPositions = new Vector3[particleCountNow];
            for (int i = 0; i < particleCountNow; i++)
            {
                Vector3 localPos = loadData.localParticles[frameCount].positions[i];
                Vector3 worldPos = localToWorld.MultiplyPoint3x4(localPos);
                particleWorldPositions[i] = worldPos;
            }

            var leftHandPositions = new float[(int)VRTRIXBones.NumOfBones / 2 - 1 + 5, 3];
            var leftHandEulerAngles = new float[(int)VRTRIXBones.NumOfBones / 2 - 1 + 5, 3];
            for (int i = (int)VRTRIXBones.L_Hand; i < (int)VRTRIXBones.R_Arm; i++)
            {
                leftHandPositions[i - (int)VRTRIXBones.L_Hand, 0] = loadData.dualHandPoses[frameCount].positions[i].x;
                leftHandPositions[i - (int)VRTRIXBones.L_Hand, 1] = loadData.dualHandPoses[frameCount].positions[i].y;
                leftHandPositions[i - (int)VRTRIXBones.L_Hand, 2] = loadData.dualHandPoses[frameCount].positions[i].z;
                leftHandEulerAngles[i - (int)VRTRIXBones.L_Hand, 0] = loadData.dualHandPoses[frameCount].eulerAngles[i].x;
                leftHandEulerAngles[i - (int)VRTRIXBones.L_Hand, 1] = loadData.dualHandPoses[frameCount].eulerAngles[i].y;
                leftHandEulerAngles[i - (int)VRTRIXBones.L_Hand, 2] = loadData.dualHandPoses[frameCount].eulerAngles[i].z;
            }
            for (int i = 0; i < 5; i++)
            {
                leftHandPositions[i + 16, 0] = handsEndBones.leftBones[i].position.x;
                leftHandPositions[i + 16, 1] = handsEndBones.leftBones[i].position.y;
                leftHandPositions[i + 16, 2] = handsEndBones.leftBones[i].position.z;
                leftHandEulerAngles[i + 16, 0] = handsEndBones.leftBones[i].eulerAngles.x;
                leftHandEulerAngles[i + 16, 1] = handsEndBones.leftBones[i].eulerAngles.y;
                leftHandEulerAngles[i + 16, 2] = handsEndBones.leftBones[i].eulerAngles.z;
            }
            var leftHandState = loadData.dualHandPoses[frameCount].leftState;
            saveAnimationData.leftHandPositions.Add(leftHandPositions);
            saveAnimationData.leftHandEulerAngles.Add(leftHandEulerAngles);
            saveAnimationData.leftHandStates.Add(leftHandState);

            var rightHandPositions = new float[(int)VRTRIXBones.NumOfBones / 2 - 1 + 5, 3];
            var rightHandEulerAngles = new float[(int)VRTRIXBones.NumOfBones / 2 - 1 + 5, 3];
            for (int i = 0; i < (int)VRTRIXBones.L_Hand; i++)
            {
                rightHandPositions[i, 0] = loadData.dualHandPoses[frameCount].positions[i].x;
                rightHandPositions[i, 1] = loadData.dualHandPoses[frameCount].positions[i].y;
                rightHandPositions[i, 2] = loadData.dualHandPoses[frameCount].positions[i].z;
                rightHandEulerAngles[i, 0] = loadData.dualHandPoses[frameCount].eulerAngles[i].x;
                rightHandEulerAngles[i, 1] = loadData.dualHandPoses[frameCount].eulerAngles[i].y;
                rightHandEulerAngles[i, 2] = loadData.dualHandPoses[frameCount].eulerAngles[i].z;
            }
            for (int i = 0; i < 5; i++)
            {
                rightHandPositions[i + 16, 0] = handsEndBones.rightBones[i].position.x;
                rightHandPositions[i + 16, 1] = handsEndBones.rightBones[i].position.y;
                rightHandPositions[i + 16, 2] = handsEndBones.rightBones[i].position.z;
                rightHandEulerAngles[i + 16, 0] = handsEndBones.rightBones[i].eulerAngles.x;
                rightHandEulerAngles[i + 16, 1] = handsEndBones.rightBones[i].eulerAngles.y;
                rightHandEulerAngles[i + 16, 2] = handsEndBones.rightBones[i].eulerAngles.z;
            }
            var rightHandState = loadData.dualHandPoses[frameCount].rightState;
            saveAnimationData.rightHandPositions.Add(rightHandPositions);
            saveAnimationData.rightHandEulerAngles.Add(rightHandEulerAngles);
            saveAnimationData.rightHandStates.Add(rightHandState);

            List<int> leftGraspIndices = new List<int>();
            foreach (var solver_idx in loadData.leftGraspSolverIndices[frameCount].indices)
            {
                float minDist = float.MaxValue;
                int bestIdx = -1;
                for (int i = 0; i < vertexCount; i++)
                {
                    var dist = Vector3.Distance(particleWorldPositions[solver_idx], vertices[i]);
                    if (dist < minDist && !leftGraspIndices.Contains(i))
                    {
                        minDist = dist;
                        bestIdx = i;
                    }
                }
                if (bestIdx >= 0)
                    leftGraspIndices.Add(bestIdx);
            }
            saveAnimationData.leftGraspIndices.Add(leftGraspIndices);

            List<int> rightGraspIndices = new List<int>();
            foreach (var solver_idx in loadData.rightGraspSolverIndices[frameCount].indices)
            {
                float minDist = float.MaxValue;
                int bestIdx = -1;
                for (int i = 0; i < vertexCount; i++)
                {
                    var dist = Vector3.Distance(particleWorldPositions[solver_idx], vertices[i]);
                    if (dist < minDist && !rightGraspIndices.Contains(i))
                    {
                        minDist = dist;
                        bestIdx = i;
                    }
                }
                if (bestIdx >= 0)
                    rightGraspIndices.Add(bestIdx);
            }
            saveAnimationData.rightGraspIndices.Add(rightGraspIndices);
        }

        void UpdateCamera()
        {
            float dist = UnityEngine.Random.Range(minCameraDist, maxCameraDist);
            float pitchAngle = UnityEngine.Random.Range(minPitchAngle, maxPitchAngle);
            float yawAngle = UnityEngine.Random.Range(-180f, 180f);

            float posX1 = dist * Mathf.Cos(yawAngle * Mathf.Deg2Rad) * Mathf.Cos(pitchAngle * Mathf.Deg2Rad) + objCenter.x;
            float posX2 = dist * Mathf.Sin(yawAngle * Mathf.Deg2Rad) * Mathf.Cos(pitchAngle * Mathf.Deg2Rad) + objCenter.x;
            float posX3 = -dist * Mathf.Cos(yawAngle * Mathf.Deg2Rad) * Mathf.Cos(pitchAngle * Mathf.Deg2Rad) + objCenter.x;
            float posX4 = -dist * Mathf.Sin(yawAngle * Mathf.Deg2Rad) * Mathf.Cos(pitchAngle * Mathf.Deg2Rad) + objCenter.x;
            float posZ1 = dist * Mathf.Sin(yawAngle * Mathf.Deg2Rad) * Mathf.Cos(pitchAngle * Mathf.Deg2Rad) + objCenter.z;
            float posZ2 = -dist * Mathf.Cos(yawAngle * Mathf.Deg2Rad) * Mathf.Cos(pitchAngle * Mathf.Deg2Rad) + objCenter.z;
            float posZ3 = -dist * Mathf.Sin(yawAngle * Mathf.Deg2Rad) * Mathf.Cos(pitchAngle * Mathf.Deg2Rad) + objCenter.z;
            float posZ4 = dist * Mathf.Cos(yawAngle * Mathf.Deg2Rad) * Mathf.Cos(pitchAngle * Mathf.Deg2Rad) + objCenter.z;
            float posY = dist * Mathf.Sin(pitchAngle * Mathf.Deg2Rad) + objCenter.y;

            List<Vector3> cameraPosList = new List<Vector3>();
            cameraPosList.Add(new Vector3(posX1, posY, posZ1));
            cameraPosList.Add(new Vector3(posX2, posY, posZ2));
            cameraPosList.Add(new Vector3(posX3, posY, posZ3));
            cameraPosList.Add(new Vector3(posX4, posY, posZ4));

            for (int i = 0; i < synths.Length; i++)
            {
                var synth = synths[i];
                synth.transform.position = cameraPosList[i];
                synth.transform.rotation = Quaternion.FromToRotation(Vector3.forward, objCenter - cameraPosList[i]);
                synth.transform.rotation = synth.transform.rotation * Quaternion.Euler(0f, 0f, -synth.transform.rotation.eulerAngles.z);
                //synth.transform.rotation = Quaternion.Euler(0f, -yawAngle, 0f) * Quaternion.Euler(pitchAngle, 0f, 0f);
            }
        }

        void SaveAllData()
        {
            isSaving = true;
            using (var fs = File.Open(saveAnimationBsonPath, FileMode.Create))
            {
                using (var writer = new BsonWriter(fs))
                {
                    var serializer = new JsonSerializer();
                    serializer.Serialize(writer, saveAnimationData);
                }
            }
            saveAnimationData.vertices.Clear();
            Debug.Log($"Saving to {saveAnimationBsonPath}!");

            if (saveImgToBson)
            {
                using (var fs = File.Open(saveImageBsonPath, FileMode.Create))
                {
                    using (var writer = new BsonWriter(fs))
                    {
                        var serializer = new JsonSerializer();
                        serializer.Serialize(writer, saveImageData);
                    }
                }
                Debug.Log($"Saving to {saveImageBsonPath}!");
            }
            isSaving = false;
        }

        void UpdateActor()
        {
            if (!isRunning)
            {
                while (true)
                {
                    if (randomShow)
                        currentIdx = UnityEngine.Random.Range(0, jsonFilenames.Count);
                    else
                        //currentIdx = 256;
                        currentIdx++;

                    if (currentIdx >= jsonFilenames.Count)
                    {
#if UNITY_EDITOR
                        UnityEditor.EditorApplication.isPlaying = false;
#else
                        Application.Quit();
#endif
                    }

                    string loadJsonPath = jsonFilenames[currentIdx];
                    Debug.Log($"{currentIdx}/{jsonFilenames.Count}: Loading {loadJsonPath}!");
                    string jsonStr = File.ReadAllText(loadJsonPath);
                    loadData = JsonUtility.FromJson<ObiSceneManagerImitation2.PoseData>(jsonStr);

                    // fix bug in saving
                    if (Mathf.Abs(loadData.planeHeight - 5f) < 1e-2)
                        loadData.planeHeight = lastPlaneHeight;
                    lastPlaneHeight = loadData.planeHeight;

                    if (loadData.planeHeight > 5.0f)
                    {
                        // fix bug of saving
                        int totalFrames = loadData.localParticles.Count;
                        objCenter = new Vector3(0f, 0f, 0f);
                        Vector3 minValue = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                        Vector3 maxValue = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                        for (int i = 0; i < particleCountNow; i++)
                        {
                            // first frame
                            Vector3 localPos = loadData.localParticles[0].positions[i];
                            Vector4 localPos4 = new Vector4(localPos.x, localPos.y, localPos.z, 1.0f);
                            Vector4 worldPos4 = loadData.solverLocalToWorldMatrices[totalFrames - 1] * localPos4;
                            Vector3 worldPos = new Vector3(worldPos4.x, worldPos4.y, worldPos4.z);
                            if (worldPos.y < loadData.planeHeight)
                                loadData.planeHeight = worldPos.y;
                            minValue.x = (worldPos.x < minValue.x) ? worldPos.x : minValue.x;
                            minValue.y = (worldPos.y < minValue.y) ? worldPos.y : minValue.y;
                            minValue.z = (worldPos.z < minValue.z) ? worldPos.z : minValue.z;
                            maxValue.x = (worldPos.x > maxValue.x) ? worldPos.x : maxValue.x;
                            maxValue.y = (worldPos.y > maxValue.y) ? worldPos.y : maxValue.y;
                            maxValue.z = (worldPos.z > maxValue.z) ? worldPos.z : maxValue.z;

                            // last frame
                            localPos = loadData.localParticles[totalFrames - 1].positions[i];
                            localPos4 = new Vector4(localPos.x, localPos.y, localPos.z, 1.0f);
                            worldPos4 = loadData.solverLocalToWorldMatrices[totalFrames - 1] * localPos4;
                            worldPos = new Vector3(worldPos4.x, worldPos4.y, worldPos4.z);
                            if (worldPos.y < loadData.planeHeight)
                                loadData.planeHeight = worldPos.y;
                            minValue.x = (worldPos.x < minValue.x) ? worldPos.x : minValue.x;
                            minValue.y = (worldPos.y < minValue.y) ? worldPos.y : minValue.y;
                            minValue.z = (worldPos.z < minValue.z) ? worldPos.z : minValue.z;
                            maxValue.x = (worldPos.x > maxValue.x) ? worldPos.x : maxValue.x;
                            maxValue.y = (worldPos.y > maxValue.y) ? worldPos.y : maxValue.y;
                            maxValue.z = (worldPos.z > maxValue.z) ? worldPos.z : maxValue.z;
                        }
                        objCenter = (minValue + maxValue) / 2;
                        loadData.planeHeight -= 0.01f;
                    }

                    particleCountNow = loadData.localParticles[0].positions.Count;
                    if (isDebug)
                    {
                        if (particleCount != particleCountNow)
                        {
                            Debug.LogError($"particle position number: {particleCountNow} for {loadJsonPath}!");
                        }

                        foreach (var obj in particles)
                            Destroy(obj);
                        particles.Clear();
                        for (int i = 0; i < particleCountNow; i++)
                        {
                            var obj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                            obj.transform.localScale = new Vector3(0.01f, 0.01f, 0.01f);
                            particles.Add(obj);
                        }

                    }

                    string[] sArr = Path.GetFileName(loadJsonPath).Replace(".json", "").Split('_');
                    string dirNum = sArr[sArr.Length - 1];
                    string objName = dirNum + $"/{type.ToLower()}";

                    // load object mesh
                    string objPath = Path.Combine(assetBundleDir, objName);
                    var ab = AssetBundle.LoadFromFile(objPath);
                    if (ab == null)
                    {
                        Debug.LogError($"Can't find {objPath}!");
                        continue;
                    }

                    if (obj != null)
                        Destroy(obj);
                    obj = Instantiate(ab.LoadAsset<GameObject>(type.ToLower()));
                    ab.Unload(false);
                    meshFilter = obj.GetComponentInChildren<MeshFilter>();
                    meshFilter.gameObject.layer = 11;
                    slaveMesh = meshFilter.mesh;
                    slaveMesh.RecalculateTangents();
                    meshRenderer = obj.GetComponentInChildren<MeshRenderer>();
                    meshRenderer.materials = materials;
                    CalcNOCS(); // Calculate NOCS coordinates for each vertex in the mesh

                    // load blueprint
                    string blueprintPath = Path.Combine(assetBundleDir,
                        objName.Replace(type.ToLower(), $"remesh_{particleCount}_{type.ToLower()}_blueprint"));
                    ab = AssetBundle.LoadFromFile(blueprintPath);
                    if (ab == null)
                    {
                        Debug.LogError($"Can't find {blueprintPath}!");
                        continue;
                    }
                    blueprint = ab.LoadAsset<ObiClothBlueprint>(Path.GetFileName(blueprintPath));
                    ab.Unload(false);

                    // load skinmap
                    string skinmapPath = Path.Combine(assetBundleDir,
                        objName.Replace(type.ToLower(), $"remesh_{particleCount}_{type.ToLower()}_skinmap"));
                    ab = AssetBundle.LoadFromFile(skinmapPath);
                    if (ab == null)
                    {
                        Debug.LogError($"Can't find {skinmapPath}!");
                        continue;
                    }
                    skinMap = ab.LoadAsset<ObiTriangleSkinMap>(Path.GetFileName(skinmapPath));
                    ab.Unload(false);

                    if (!skinMap.bound || skinMap.skinnedVertices.Count < slaveMesh.vertexCount)
                    {
                        Debug.LogError($"Missing skinned vertices in {skinmapPath}!");
                        continue;
                    }

                    plane.transform.position = new Vector3(0, loadData.planeHeight, 0);
                    int vertexNum = plane.GetComponent<MeshFilter>().mesh.vertexCount;
                    plane.GetComponent<MeshFilter>().mesh.SetUVs(1, new Vector3[vertexNum]);

                    saveAnimationData = new AnimationData(slaveMesh.vertexCount, slaveMesh.triangles.Length);
                    string verticesDir = Path.Combine(saveDir, dataPrefix, "animation");
                    if (!System.IO.Directory.Exists(verticesDir))
                        System.IO.Directory.CreateDirectory(verticesDir);
                    saveAnimationBsonPath = Path.Combine(verticesDir, Path.GetFileNameWithoutExtension(loadJsonPath) + ".bson");

                    saveImageData = new ImageData();
                    string imgDir = Path.Combine(saveDir, dataPrefix, "image");
                    if (!System.IO.Directory.Exists(imgDir))
                        System.IO.Directory.CreateDirectory(imgDir);
                    saveImageBsonPath = Path.Combine(imgDir, Path.GetFileNameWithoutExtension(loadJsonPath) + ".bson");

                    break;
                }
            }
        }

    
        void UpdateParticles()
        {
            for (int i = 0; i < particleCountNow; i++)
            {
                Vector3 localPos = loadData.localParticles[frameCount].positions[i];
                Vector4 localPos4 = new Vector4(localPos.x, localPos.y, localPos.z, 1.0f);
                Vector4 worldPos4 = loadData.solverLocalToWorldMatrices[frameCount] * localPos4;
                Vector3 worldPos = new Vector3(worldPos4.x, worldPos4.y, worldPos4.z);
                particles[i].transform.position = worldPos;

                int solver_idx = loadData.solverIndices[i];
                if (loadData.leftGraspSolverIndices[frameCount].indices.Contains(solver_idx))
                    particles[i].GetComponent<MeshRenderer>().material.color = Color.red;
                else if (loadData.rightGraspSolverIndices[frameCount].indices.Contains(solver_idx))
                    particles[i].GetComponent<MeshRenderer>().material.color = Color.blue;
                else
                    particles[i].GetComponent<MeshRenderer>().material.color = Color.white;
            }
        }

        void UpdateSkinning()
        {
            //if (skinMap.bound && slaveMesh != null && isRunning)
            if (slaveMesh != null && isRunning)
            {
                Matrix4x4 s2l = loadData.solverLocalToWorldMatrices[frameCount];

                slavePositions = slaveMesh.vertices;
                slaveNormals = slaveMesh.normals;
                slaveTangents = slaveMesh.tangents;

                Vector3 skinnedPos = Vector3.zero;
                Vector3 skinnedNormal = Vector3.zero;
                Vector3 skinnedTangent = Vector3.zero;

                for (int i = 0; i < skinMap.skinnedVertices.Count; ++i)
                {
                    var data = skinMap.skinnedVertices[i];
                    int firstVertex = data.masterTriangleIndex;

                    int t1 = blueprint.deformableTriangles[firstVertex];
                    int t2 = blueprint.deformableTriangles[firstVertex + 1];
                    int t3 = blueprint.deformableTriangles[firstVertex + 2];

                    // get solver indices for each particle:
                    int s1 = loadData.solverIndices[t1];
                    int s2 = loadData.solverIndices[t2];
                    int s3 = loadData.solverIndices[t3];

                    // get master particle positions/normals:
                    Vector3 p1 = loadData.localParticles[frameCount].positions[s1];
                    Vector3 p2 = loadData.localParticles[frameCount].positions[s2];
                    Vector3 p3 = loadData.localParticles[frameCount].positions[s3];

                    Vector3 n1 = loadData.localParticles[frameCount].normals[s1];
                    Vector3 n2 = loadData.localParticles[frameCount].normals[s2];
                    Vector3 n3 = loadData.localParticles[frameCount].normals[s3];

                    ObiVectorMath.BarycentricInterpolation(p1, p2, p3, n1, n2, n3, data.position.barycentricCoords, data.position.height, ref skinnedPos);

                    ObiVectorMath.BarycentricInterpolation(p1, p2, p3, n1, n2, n3, data.normal.barycentricCoords, data.normal.height, ref skinnedNormal);

                    ObiVectorMath.BarycentricInterpolation(p1, p2, p3, n1, n2, n3, data.tangent.barycentricCoords, data.tangent.height, ref skinnedTangent);

                    // update slave data arrays:
                    slavePositions[data.slaveIndex] = s2l.MultiplyPoint3x4(skinnedPos);
                    slaveNormals[data.slaveIndex] = s2l.MultiplyVector(skinnedNormal - skinnedPos);

                    Vector3 tangent = s2l.MultiplyVector(skinnedTangent - skinnedPos);
                    slaveTangents[data.slaveIndex] = new Vector4(tangent.x, tangent.y, tangent.z, slaveTangents[data.slaveIndex].w);
                }

                slaveMesh.vertices = slavePositions;
                slaveMesh.normals = slaveNormals;
                slaveMesh.tangents = slaveTangents;
                slaveMesh.RecalculateBounds();
            }
        }

        void UpdateHands()
        {
            handsBoneMapping.MapToVRTRIX_BoneName("L_Arm").transform.position = loadData.dualHandPoses[frameCount].positions[(int)VRTRIXBones.L_Arm];
            handsBoneMapping.MapToVRTRIX_BoneName("R_Arm").transform.position = loadData.dualHandPoses[frameCount].positions[(int)VRTRIXBones.R_Arm];
            for (int i = 0; i < (int)VRTRIXBones.R_Arm; i++)
                handsBoneMapping.MyCharacterFingers[i].eulerAngles = loadData.dualHandPoses[frameCount].eulerAngles[i];
        }

        void CalcNOCS()
        {
            if (slaveMesh != null)
            {
                Vector3[] vertices = slaveMesh.vertices;
                Vector3 minValue = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
                Vector3 maxValue = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
                for (var i = 0; i < vertices.Length; i++)
                {
                    if (vertices[i].x < minValue.x)
                        minValue.x = vertices[i].x;
                    if (vertices[i].y < minValue.y)
                        minValue.y = vertices[i].y;
                    if (vertices[i].z < minValue.z)
                        minValue.z = vertices[i].z;

                    if (vertices[i].x > maxValue.x)
                        maxValue.x = vertices[i].x;
                    if (vertices[i].y > maxValue.y)
                        maxValue.y = vertices[i].y;
                    if (vertices[i].z > maxValue.z)
                        maxValue.z = vertices[i].z;
                }
                float scale = Mathf.Sqrt((maxValue.x - minValue.x) * (maxValue.x - minValue.x) +
                    (maxValue.y - minValue.y) * (maxValue.y - minValue.y) +
                    (maxValue.z - minValue.z) * (maxValue.z - minValue.z));
                Vector3[] nocs_list = new Vector3[vertices.Length];
                Vector3 center = (minValue + maxValue) / 2;
                Vector3 minNOCS = new Vector3(10.0f, 10.0f, 10.0f);
                Vector3 maxNOCS = new Vector3(-10.0f, -10.0f, -10.0f);
                for (var i = 0; i < vertices.Length; i++)
                {
                    //nocs_list[i] = (vertices[i] - minValue) / scale + new Vector3(0.5f, 0.5f, 0.5f) - center / scale;
                    nocs_list[i] = (vertices[i] - center) / scale + new Vector3(0.5f, 0.5f, 0.5f);
                    minNOCS = Vector3.Min(minNOCS, nocs_list[i]);
                    maxNOCS = Vector3.Max(maxNOCS, nocs_list[i]);
                }
                Debug.Log($"min NOCS: {minNOCS}, max NOCS: {maxNOCS}");

                Vector2[] nocs_split1 = new Vector2[nocs_list.Length];
                Vector2[] nocs_split2 = new Vector2[nocs_list.Length];
                for (var i = 0; i < vertices.Length; i++)
                {
                    nocs_split1[i] = new Vector2(nocs_list[i].x, nocs_list[i].y);
                    nocs_split2[i] = new Vector2(nocs_list[i].z, 0);
                }
                slaveMesh.SetUVs(1, nocs_split1);
                slaveMesh.SetUVs(2, nocs_split2);
            }
        }
    }
}