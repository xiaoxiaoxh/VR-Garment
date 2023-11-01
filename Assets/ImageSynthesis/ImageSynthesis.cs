using UnityEngine;
using UnityEngine.Rendering;
using System.Collections;
using System.IO;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

// @TODO:
// . support custom color wheels in optical flow via lookup textures
// . support custom depth encoding
// . support multiple overlay cameras
// . tests
// . better example scene(s)

// @KNOWN ISSUES
// . Motion Vectors can produce incorrect results in Unity 5.5.f3 when
//      1) during the first rendering frame
//      2) rendering several cameras with different aspect ratios - vectors do stretch to the sides of the screen

[RequireComponent(typeof(Camera))]
public class ImageSynthesis : MonoBehaviour
{

	// pass configuration
	private CapturePass[] capturePasses = new CapturePass[] {
		new CapturePass() { name = "_img" },
		new CapturePass() { name = "_id", supportsAntialiasing = false },
		new CapturePass() { name = "_layer", supportsAntialiasing = false },
		new CapturePass() { name = "_depth", supportsAntialiasing = false },
		new CapturePass() { name = "_normals", supportsAntialiasing = false },
		new CapturePass() { name = "_flow", supportsAntialiasing = false, needsRescale = true }, // (see issue with Motion Vectors in @KNOWN ISSUES)
		new CapturePass() { name = "_nocs", supportsAntialiasing = false },
	};

	struct CapturePass
	{
		// configuration
		public string name;
		public bool supportsAntialiasing;
		public bool needsRescale;
		public CapturePass(string name_) { name = name_; supportsAntialiasing = true; needsRescale = false; camera = null; }

		// impl
		public Camera camera;
	};

	public Shader uberReplacementShader;
	public Shader opticalFlowShader;
	public Shader nocsShader;

	public float opticalFlowSensitivity = 1.0f;
	public bool grayscale = false;

	// cached materials
	private Material opticalFlowMaterial;

	void Start()
	{
		// default fallbacks, if shaders are unspecified
		if (!uberReplacementShader)
			uberReplacementShader = Shader.Find("Hidden/UberReplacement");

		if (!opticalFlowShader)
			opticalFlowShader = Shader.Find("Hidden/OpticalFlow");

		if (!nocsShader)
			nocsShader = Shader.Find("Hidden/NOCS");

		// use real camera to capture final image
		capturePasses[0].camera = GetComponent<Camera>();
		for (int q = 1; q < capturePasses.Length; q++)
			capturePasses[q].camera = CreateHiddenCamera(capturePasses[q].name);

		OnCameraChange();
		OnSceneChange(grayscale);
	}

	void LateUpdate()
	{
#if UNITY_EDITOR
		if (DetectPotentialSceneChangeInEditor())
			OnSceneChange(grayscale);
#endif // UNITY_EDITOR

		// @TODO: detect if camera properties actually changed
		OnCameraChange();
	}

	private Camera CreateHiddenCamera(string name)
	{
		var go = new GameObject(name, typeof(Camera));
		go.hideFlags = HideFlags.HideAndDontSave;
		go.transform.parent = transform;

		var newCamera = go.GetComponent<Camera>();
		return newCamera;
	}


	static private void SetupCameraWithReplacementShader(Camera cam, Shader shader, ReplacelementModes mode)
	{
		SetupCameraWithReplacementShader(cam, shader, mode, Color.black);
	}

	static private void SetupCameraWithReplacementShader(Camera cam, Shader shader, ReplacelementModes mode, Color clearColor)
	{
		var cb = new CommandBuffer();
		cb.SetGlobalFloat("_OutputMode", (int)mode); // @TODO: CommandBuffer is missing SetGlobalInt() method
		cam.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, cb);
		cam.AddCommandBuffer(CameraEvent.BeforeFinalPass, cb);
		cam.SetReplacementShader(shader, "");
		cam.backgroundColor = clearColor;
		cam.clearFlags = CameraClearFlags.SolidColor;
	}

	static private void SetupCameraWithPostShader(Camera cam, Material material, DepthTextureMode depthTextureMode = DepthTextureMode.None)
	{
		var cb = new CommandBuffer();
		cb.Blit(null, BuiltinRenderTextureType.CurrentActive, material);
		cam.AddCommandBuffer(CameraEvent.AfterEverything, cb);
		cam.depthTextureMode = depthTextureMode;
	}

	enum ReplacelementModes
	{
		ObjectId = 0,
		CatergoryId = 1,
		DepthCompressed = 2,
		DepthMultichannel = 3,
		Normals = 4,
		NOCS = 6
	};

	public void OnCameraChange()
	{
		int targetDisplay = 1;
		var mainCamera = GetComponent<Camera>();
		foreach (var pass in capturePasses)
		{
			if (pass.camera == mainCamera)
				continue;

			// cleanup capturing camera
			pass.camera.RemoveAllCommandBuffers();

			// copy all "main" camera parameters into capturing camera
			pass.camera.CopyFrom(mainCamera);

			// set antiAliasing parameter
			pass.camera.allowMSAA = pass.supportsAntialiasing;

			// set targetDisplay here since it gets overriden by CopyFrom()
			pass.camera.targetDisplay = targetDisplay++;
		}

		// cache materials and setup material properties
		if (!opticalFlowMaterial || opticalFlowMaterial.shader != opticalFlowShader)
			opticalFlowMaterial = new Material(opticalFlowShader);
		opticalFlowMaterial.SetFloat("_Sensitivity", opticalFlowSensitivity);

		// setup command buffers and replacement shaders
		SetupCameraWithReplacementShader(capturePasses[1].camera, uberReplacementShader, ReplacelementModes.ObjectId, Color.white);
		SetupCameraWithReplacementShader(capturePasses[2].camera, uberReplacementShader, ReplacelementModes.CatergoryId, Color.white);
		SetupCameraWithReplacementShader(capturePasses[3].camera, uberReplacementShader, ReplacelementModes.DepthCompressed, Color.black);
		//SetupCameraWithReplacementShader(capturePasses[3].camera, uberReplacementShader, ReplacelementModes.DepthMultichannel, Color.black);
		SetupCameraWithReplacementShader(capturePasses[4].camera, uberReplacementShader, ReplacelementModes.Normals);
		SetupCameraWithPostShader(capturePasses[5].camera, opticalFlowMaterial, DepthTextureMode.Depth | DepthTextureMode.MotionVectors);
		SetupCameraWithReplacementShader(capturePasses[6].camera, nocsShader, ReplacelementModes.NOCS, Color.black);
	}


	public void OnSceneChange(bool grayscale = false)
	{
		var renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
		var mpb = new MaterialPropertyBlock();
		foreach (var r in renderers)
		{
			var id = r.gameObject.GetInstanceID();
			var layer = r.gameObject.layer;
			var tag = r.gameObject.tag;

			mpb.SetColor("_ObjectColor", ColorEncoding.EncodeIDAsColor(id));
			//mpb.SetColor("_ObjectColor", ColorEncoding.EncodeTagAsColor(tag));
			mpb.SetColor("_CategoryColor", ColorEncoding.EncodeLayerAsColor(layer, grayscale));
			r.SetPropertyBlock(mpb);
		}
	}

	public void Save(string filename, int width = -1, int height = -1, string path = "", int specificPass = -1,
		Action<string, string> onSave = null)
	{
		if (width <= 0 || height <= 0)
		{
			width = Screen.width;
			height = Screen.height;
		}

		var filenameExtension = System.IO.Path.GetExtension(filename);
		if (filenameExtension == "")
			if (specificPass == 0)
				filenameExtension = ".jpg";
			else if (specificPass == 3)
				filenameExtension = ".exr";
			else
				filenameExtension = ".png";
		var filenameWithoutExtension = Path.GetFileNameWithoutExtension(filename);

		var pathWithoutExtension = Path.Combine(path, filenameWithoutExtension);
		if (!System.IO.Directory.Exists(path) && onSave == null)
			System.IO.Directory.CreateDirectory(path);

		// execute as coroutine to wait for the EndOfFrame before starting capture
		StartCoroutine(
			WaitForEndOfFrameAndSave(pathWithoutExtension, filenameExtension, width, height, specificPass, onSave));
	}

	private IEnumerator WaitForEndOfFrameAndSave(string filenameWithoutExtension, string filenameExtension, int width, int height, int specificPass,
		Action<string, string> onSave=null)
	{
		yield return new WaitForEndOfFrame();
		Save(filenameWithoutExtension, filenameExtension, width, height, specificPass, onSave);		
	}

	private void Save(string filenameWithoutExtension, string filenameExtension, int width, int height, int specificPass,
		Action<string, string> onSave = null)
	{
		if (specificPass == -1)
		{
			int idx = 0;
			foreach (var pass in capturePasses)
				//Save(pass.camera, filenameWithoutExtension + pass.name + filenameExtension, width, height, pass.supportsAntialiasing, pass.needsRescale);
				Save(pass.camera, filenameWithoutExtension + filenameExtension, width, height, pass.supportsAntialiasing, pass.needsRescale, idx++, onSave);
		}
		else
		{
			//var pass = capturePasses[0];
			//Save(pass.camera, filenameWithoutExtension + pass.name + filenameExtension, width, height, pass.supportsAntialiasing, pass.needsRescale);
			var pass = capturePasses[specificPass];
			Save(pass.camera, filenameWithoutExtension + filenameExtension, width, height, pass.supportsAntialiasing, pass.needsRescale, specificPass, onSave);
		}
	}

	private void Save(Camera cam, string filename, int width, int height, bool supportsAntialiasing, bool needsRescale, int specificPass,
		Action<string, string> onSave = null)
	{
		var mainCamera = GetComponent<Camera>();
		var depth = 24;
		bool isDepth = (specificPass == 3);
		bool isColor = (specificPass == 0);
		RenderTextureFormat format;
		RenderTextureReadWrite readWrite;
		if (isDepth)
		{
			format = RenderTextureFormat.ARGBFloat;
			readWrite = RenderTextureReadWrite.Linear;
		}
		else
		{
			format = RenderTextureFormat.Default;
			readWrite = RenderTextureReadWrite.Default;
		}
		var antiAliasing = (supportsAntialiasing) ? Mathf.Max(1, QualitySettings.antiAliasing) : 1;

		var finalRT =
			RenderTexture.GetTemporary(width, height, depth, format, readWrite, antiAliasing);
		var renderRT = (!needsRescale) ? finalRT :
			RenderTexture.GetTemporary(mainCamera.pixelWidth, mainCamera.pixelHeight, depth, format, readWrite, antiAliasing);
		Texture2D tex;
		if (isDepth)
			tex = new Texture2D(width, height, TextureFormat.RGBAFloat, false);
		else
			tex = new Texture2D(width, height, TextureFormat.RGB24, false);


		var prevActiveRT = RenderTexture.active;
		var prevCameraRT = cam.targetTexture;

		// render to offscreen texture (readonly from CPU side)
		RenderTexture.active = renderRT;
		cam.targetTexture = renderRT;

		cam.Render();

		if (needsRescale)
		{
			// blit to rescale (see issue with Motion Vectors in @KNOWN ISSUES)
			RenderTexture.active = finalRT;
			Graphics.Blit(renderRT, finalRT);
			RenderTexture.ReleaseTemporary(renderRT);
		}

		// read offsreen texture contents into the CPU readable texture
		tex.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
		tex.Apply();

		if (isDepth)
		{
			// encode texture into EXR
			var bytes = tex.EncodeToEXR(Texture2D.EXRFlags.CompressRLE);
			if (onSave != null)
            {
				onSave(filename, Convert.ToBase64String(bytes));
			}			
			else
				File.WriteAllBytes(filename, bytes);
		}
		else if (isColor)
		{
			// encode texture into JPG
			var bytes = tex.EncodeToJPG(95);
			if (onSave != null)
			{
				onSave(filename, Convert.ToBase64String(bytes));
			}
			else
				File.WriteAllBytes(filename, bytes);
		}
		else
		{
			// encode texture into PNG
			var bytes = tex.EncodeToPNG();
			if (onSave != null)
			{
				onSave(filename, Convert.ToBase64String(bytes));
			}
			else
				File.WriteAllBytes(filename, bytes);
		}
		// restore state and cleanup
		cam.targetTexture = prevCameraRT;
		RenderTexture.active = prevActiveRT;

		UnityEngine.Object.Destroy(tex);
		RenderTexture.ReleaseTemporary(finalRT);
	}

#if UNITY_EDITOR
	private GameObject lastSelectedGO;
	private int lastSelectedGOLayer = -1;
	private string lastSelectedGOTag = "unknown";
	private bool DetectPotentialSceneChangeInEditor()
	{
		bool change = false;
		// there is no callback in Unity Editor to automatically detect changes in scene objects
		// as a workaround lets track selected objects and check, if properties that are 
		// interesting for us (layer or tag) did not change since the last frame
		if (UnityEditor.Selection.transforms.Length > 1)
		{
			// multiple objects are selected, all bets are off!
			// we have to assume these objects are being edited
			change = true;
			lastSelectedGO = null;
		}
		else if (UnityEditor.Selection.activeGameObject)
		{
			var go = UnityEditor.Selection.activeGameObject;
			// check if layer or tag of a selected object have changed since the last frame
			var potentialChangeHappened = lastSelectedGOLayer != go.layer || lastSelectedGOTag != go.tag;
			if (go == lastSelectedGO && potentialChangeHappened)
				change = true;

			lastSelectedGO = go;
			lastSelectedGOLayer = go.layer;
			lastSelectedGOTag = go.tag;
		}

		return change;
	}
#endif // UNITY_EDITOR
}
