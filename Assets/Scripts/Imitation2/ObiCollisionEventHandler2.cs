using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Obi;
using VRTRIX;

[RequireComponent(typeof(ObiSolver))]
public class ObiCollisionEventHandler2 : MonoBehaviour
{
	ObiSolver solver;
	public GameObject hands;
	public int maxPinnedVertexNum = 8;
	public ObiColliderBase leftFingerTailCol;
	public ObiColliderBase rightFingerTailCol;
	public ObiColliderBase graspSphereCol;
	public List<int> leftHeldParticles, rightHeldParticles;
	public GameObject leftRobotBase, rightRobotBase;
	public float robotSelfMinRange = 0.2f;
	public float robotOtherMinRange = 0.15f;
	public float robotMaxRange = 0.84f;
	private bool isLeftHandHeld=false;
	private bool isRightHandHeld=false;
	[HideInInspector]
	public bool isGraspSpherePinned = false;
	[HideInInspector]
	public bool releaseGraspSphere = false;
	private Queue<VRTRIXGloveGesture> leftHandRecord;
	private Queue<VRTRIXGloveGesture> rightHandRecord;
	private List<ObiActor> heldActors;
	private VRTRIXGloveDataStreaming gloveData;	

	// temporal variables
	private Oni.Contact validContact;
	private ObiColliderBase col;
	[HideInInspector]
	public ObiColliderWorld world;
	private List<int> vertexList;
	public enum ColliderType
	{
		None,
		LeftHandIndexTail,
		LeftHandOther,
		RightHandIndexTail,
		RightHandOther,
		Sphere
	}

    void Awake()
	{
		leftHandRecord = new Queue<VRTRIXGloveGesture>();
		rightHandRecord = new Queue<VRTRIXGloveGesture>();
		heldActors = new List<ObiActor>();
		leftHeldParticles = new List<int>();
		rightHeldParticles = new List<int>();

		solver = GetComponent<ObiSolver>();
		world = ObiColliderWorld.GetInstance();
		gloveData = hands.GetComponent<VRTRIXGloveDataStreaming>();
	}

	void OnEnable()
	{
		solver.OnCollision += Solver_OnCollision;
	}

	void OnDisable()
	{
		solver.OnCollision -= Solver_OnCollision;
	}

    void Update()
    {
		UpdateGesture();
	}

    void FixedUpdate()
    {
		if (isLeftHandHeld && IsHandRelease(true))
			RemovePinConstrains(true);

		if (isRightHandHeld && IsHandRelease(false))
			RemovePinConstrains(false);

		if (isGraspSpherePinned && releaseGraspSphere)
        {
			RemovePinConstrains(true, true);
		}
			
	}

    void UpdateGesture()
    {
		leftHandRecord.Enqueue(gloveData.GetGesture(HANDTYPE.LEFT_HAND));
		if (leftHandRecord.Count > 5)
			leftHandRecord.Dequeue();

		rightHandRecord.Enqueue(gloveData.GetGesture(HANDTYPE.RIGHT_HAND));
		if (rightHandRecord.Count > 5)
			rightHandRecord.Dequeue();
	}

    public void Reset()
    {
		if (heldActors != null)
        {
			heldActors.Clear();
			leftHeldParticles.Clear();
			rightHeldParticles.Clear();
			leftHandRecord.Clear();
			rightHandRecord.Clear();
			isLeftHandHeld = false;
			isRightHandHeld = false;
			world = ObiColliderWorld.GetInstance();
		}
		isGraspSpherePinned = false;
		releaseGraspSphere = false;
	}

    private void RemovePinConstrains(bool isLeftHand, bool isSphere=false)
	{
		// get constraints in each actor:
		foreach (var actor in heldActors)
        {
			if (actor == null)
            {
				Debug.Log("Failed to remove pin constraints!");
				break;
			}
				
			var pinConstraints = actor.GetConstraintsByType(Oni.ConstraintType.Pin)
				as ObiConstraints<ObiPinConstraintsBatch>;			
			
			bool isRemoved = false;
			foreach (var batch in pinConstraints.batches)
			{
				List<int> removedIndexList = new List<int>();
				for (int i = 0; i < batch.constraintCount; ++i)
				{
					int particle_index = batch.particleIndices[i];
					int col_index = batch.colliderIndices[i];
					ObiColliderBase col = world.colliderHandles[col_index].owner;
					ColliderType col_type = FindColliderType(col.gameObject);
					if (col_type == ColliderType.Sphere && isSphere)
					{
						isRemoved = true;
						removedIndexList.Add(i);
						solver.filters[particle_index] ^= 0b00000000000100000000000000000000;
					}
					else if (((col_type == ColliderType.LeftHandIndexTail || col_type == ColliderType.LeftHandOther) && isLeftHand) ||
						((col_type == ColliderType.RightHandIndexTail || col_type == ColliderType.RightHandOther) && !isLeftHand))
					{
						isRemoved = true;
						removedIndexList.Add(i);
						// Set collision mask back to normal;
						solver.filters[particle_index] ^= 0b00000000000010000000000000000000;
					}
				}
				int removeCount = 0;
				foreach (var index in removedIndexList)
				{
					batch.RemoveConstraint(index - removeCount);
					removeCount++;
				}
			}

			if (isSphere)
            {
				if (isRemoved)
				{
					// this will cause the solver to rebuild pin constraints at the beginning of the next frame:
					actor.SetConstraintsDirty(Oni.ConstraintType.Pin);
					Debug.Log($"Remove grasp sphere pin constrains!");
					isGraspSpherePinned = false;
					heldActors.Remove(actor);
					break;
				}
			}
            else
            {
				if (isLeftHand)
					leftHeldParticles.Clear();
				else
					rightHeldParticles.Clear();

				if (isRemoved)
				{
					// this will cause the solver to rebuild pin constraints at the beginning of the next frame:
					actor.SetConstraintsDirty(Oni.ConstraintType.Pin);
					Debug.Log($"Remove {(isLeftHand ? "left" : "right")} hand pin constrains " +
						$"for actor {actor.gameObject.name}!");
					heldActors.Remove(actor);

					if (isLeftHand)
						isLeftHandHeld = false;
					else
						isRightHandHeld = false;
					break;
				}
			}			
		}

	}

	public List<int> FindVertices(ObiActor actor, ObiColliderBase col)
	{
		List<int> vertexList = new List<int>();
		List<float> distList = new List<float>();
		float minDistance = float.PositiveInfinity;
		int maxDistIdx = 0;
		float maxDistance = float.NegativeInfinity;
		for (int i = 0; i < actor.solverIndices.Length; ++i)
		{

			int solverIndex = actor.solverIndices[i];
			float distance = Vector3.Distance(actor.GetParticlePosition(solverIndex), col.transform.position);
			// if the particle is visually close enough to the collider, add it to the list.
			if (vertexList.Count < maxPinnedVertexNum)
            {
				vertexList.Add(solverIndex);
				distList.Add(distance);
				if (distance > maxDistance)
                {
					maxDistance = distance;
					maxDistIdx = distList.Count - 1;
				}					
				minDistance = maxDistance;
            }
			else if (vertexList.Count == maxPinnedVertexNum && distance < minDistance)
			{
				vertexList.RemoveAt(maxDistIdx);
				distList.RemoveAt(maxDistIdx);
				vertexList.Add(solverIndex);
				distList.Add(distance);
				maxDistance = float.NegativeInfinity;
				for (int idx = 0; idx < maxPinnedVertexNum; idx++)
                {
					if (distList[idx] > maxDistance)
                    {
						maxDistance = distList[idx];
						maxDistIdx = idx;
                    }
                }
				minDistance = maxDistance;
			}
		}
		//foreach (var vertex in vertexList)
		//	Debug.Log($"Find vertex {vertex}!");
		return vertexList;
	}

	void Solver_OnCollision(object sender, Obi.ObiSolver.ObiCollisionEventArgs e)
	{
		bool isValidContact = false;
		bool isLeft = true;
		bool isSphere = false;
		validContact = e.contacts[0];

		// just iterate over all contacts in the current frame:
		foreach (Oni.Contact contact in e.contacts)
		{
			if (Mathf.Abs(contact.distance) > 0.03)
				continue;
			col = world.colliderHandles[contact.bodyB].owner;
			ColliderType tmp_col_type = FindColliderType(col.gameObject);

			if (tmp_col_type == ColliderType.Sphere && !isGraspSpherePinned && !releaseGraspSphere)
            {
				isValidContact = true;
				validContact = contact;
				isSphere = true;
				break;
			}			
			else if ((((tmp_col_type == ColliderType.LeftHandIndexTail || tmp_col_type == ColliderType.LeftHandOther) && !isLeftHandHeld) ||
				((tmp_col_type == ColliderType.RightHandIndexTail || tmp_col_type == ColliderType.RightHandOther) && !isRightHandHeld)))
			{
				// if this one is an actual collision with a new finger tip, then record the nearest contact								
				isLeft = (tmp_col_type == ColliderType.LeftHandIndexTail
							|| tmp_col_type == ColliderType.LeftHandOther) ? true : false;				
				isValidContact = true;
				validContact = contact;
				break;			
			}
		}

		if (isValidContact && ((IsHandPinch(isLeft) && IsGraspPointValid(col.gameObject, isLeft)) || isSphere))
		{
			col = world.colliderHandles[validContact.bodyB].owner;
			ObiColliderBase targetCol;			
			Debug.Log($"Collide with {col.gameObject.name}");

			if (isSphere)
            {
				targetCol = col;
				isGraspSpherePinned = true;
				Debug.Log("Attach to grasp sphere!");
			}
            else
            {
				targetCol = FindFingerTailCollider(col.gameObject);
				if (isLeft)
				{
					isLeftHandHeld = true;
					Debug.Log("Attach to left hand!");
				}
				else
				{
					isRightHandHeld = true;
					Debug.Log("Attach to right hand!");
				}
			}			

			// retrieve the offset and size of the simplex in the solver.simplices array:
			int simplexStart = solver.simplexCounts.GetSimplexStartAndSize(validContact.bodyA, out int simplexSize);

			// find the cloth actor that these particles belong to
			ObiSolver.ParticleInActor pa = solver.particleToActor[simplexStart];
			ObiActor actor = pa.actor as ObiActor;

			// find the nearest vertices
			vertexList = FindVertices(actor, col);

			if (!isSphere)
            {
				if (isLeft)
					leftHeldParticles = vertexList;
				else
					rightHeldParticles = vertexList;
			}
			

			// Add current actor to heldActors list
			heldActors.Add(actor);

			// get a hold of the constraint type we want, in this case, pin constraints:
			var pinConstraints = actor.GetConstraintsByType(Oni.ConstraintType.Pin) as ObiConstraints<ObiPinConstraintsBatch>;

			// create a new pin constraints batch
			var batch = new ObiPinConstraintsBatch();
			// starting at simplexStart, iterate over all particles in the simplex:
			foreach(var particleIndex in vertexList)
			{
				// Add a couple constraints to it, pinning the nearest particles in the cloth:
				batch.AddConstraint(particleIndex, targetCol, Vector3.zero, Quaternion.identity, 0, 0, float.PositiveInfinity);
				if (isSphere)
					// Set collision mask to avoid collision with colliders of hands
					solver.filters[particleIndex] ^= 0b00000000000100000000000000000000;
				else
					// Set collision mask to avoid collision with colliders of hands
					solver.filters[particleIndex] ^= 0b00000000000010000000000000000000;
			}
			// set the amount of active constraints in the batch to simplexSize (the ones we just added).
			batch.activeConstraintCount += vertexList.Count;

			// append the batch to the pin constraints:
			pinConstraints.AddBatch(batch);

			// this will cause the solver to rebuild pin constraints at the beginning of the next frame:
			actor.SetConstraintsDirty(Oni.ConstraintType.Pin);
		}
		else if (isValidContact && IsHandPinch(isLeft) && !IsGraspPointValid(col.gameObject, isLeft))
        {
			var str = isLeft ? "left" : "right";
			Debug.Log($"Invalid grasp point for {str} hand!");
			gloveData.OnVibrate(isLeft ? HANDTYPE.LEFT_HAND : HANDTYPE.RIGHT_HAND);
		}
	}

	bool IsGraspPointValid(GameObject colObj, bool isLeftHand=false)
    {
		Vector3 colPos = colObj.transform.position;
		var leftDist = Vector3.Distance(colPos, leftRobotBase.transform.position);
		var rightDist = Vector3.Distance(colPos, rightRobotBase.transform.position);
		if (isLeftHand && leftDist <= robotMaxRange && leftDist >= robotSelfMinRange && rightDist >= robotOtherMinRange)
			return true;
		else if (!isLeftHand && rightDist <= robotMaxRange && rightDist >= robotSelfMinRange && leftDist >= robotOtherMinRange)
			return true;
		return false;
    }

	public ColliderType FindColliderType(GameObject obj)
    {		
		ColliderType col_type = ColliderType.None;
		if (obj.layer == 17)
        {
			if (obj.name.Contains("index_l_end"))
            {
				col_type = ColliderType.LeftHandIndexTail;
            }
            else if (obj.name.Contains("index_r_end"))
			{
				col_type = ColliderType.RightHandIndexTail;
			}			
		}
		else if (obj.name.Contains("Sphere"))
		{
			col_type = ColliderType.Sphere;
		}
		return col_type;
    }

	ObiColliderBase FindFingerTailCollider(GameObject obj)
    {
		if (obj.layer == 17)
		{
			if (obj.name.Contains("l_end"))
			{
				return leftFingerTailCol;
			}
			else if (obj.name.Contains("r_end"))
			{
				return rightFingerTailCol;
			}
		}
		return null;
	}

	bool IsHandPinch(bool isLeftHand)
    {
		if (isLeftHand)
        {
			foreach (var state in leftHandRecord)
				if ((state & VRTRIXGloveGesture.BUTTONPINCH) > 0)
					return true;
			return false;
		}
        else
        {
			foreach (var state in rightHandRecord)
				if ((state & VRTRIXGloveGesture.BUTTONPINCH) > 0)
					return true;
			return false;
		}
	}

	bool IsHandRelease(bool isLeftHand)
	{
		if (isLeftHand)
		{
			foreach (var state in leftHandRecord)
				if ((state & VRTRIXGloveGesture.BUTTONPINCH) > 0)
					return false;
			return true;
		}
		else
		{
			foreach (var state in rightHandRecord)
				if ((state & VRTRIXGloveGesture.BUTTONPINCH) > 0)
					return false;
			return true;
		}
	}

	//void Solver_OnCollision(object sender, Obi.ObiSolver.ObiCollisionEventArgs e)
	//{
	//	ObiColliderBase col;
	//	bool isValidContact = false;
	//	bool isLeft = true;
	//	var world = ObiColliderWorld.GetInstance();
	//	float minDistanceTail = float.PositiveInfinity;
	//	float minDistanceAll = float.PositiveInfinity;
	//	Oni.Contact nearestContactTail = e.contacts[0];
	//	Oni.Contact nearestContactAll = e.contacts[0];
	//	// just iterate over all contacts in the current frame:
	//	foreach (Oni.Contact contact in e.contacts)
	//	{
	//		col = world.colliderHandles[contact.bodyB].owner;
	//		ColliderType tmp_col_type = FindColliderType(col.gameObject);

	//		// if this one is an actual collision with a new finger tip, then record the nearest contact
	//		if ((((tmp_col_type == ColliderType.LeftHandIndexTail || tmp_col_type == ColliderType.LeftHandOther) && !isLeftHandHeld) ||
	//			((tmp_col_type == ColliderType.RightHandIndexTail || tmp_col_type == ColliderType.RightHandOther) && !isRightHandHeld))
	//			&& Mathf.Abs(contact.distance) < 0.02 && contact.distance < minDistanceAll)
	//           {
	//			isValidContact = true;
	//			minDistanceAll = contact.distance;
	//			nearestContactAll = contact;
	//			isLeft = (tmp_col_type == ColliderType.LeftHandIndexTail
	//					|| tmp_col_type == ColliderType.LeftHandOther) ? true : false;
	//			if ((tmp_col_type == ColliderType.LeftHandIndexTail || tmp_col_type == ColliderType.RightHandIndexTail)
	//			 && contact.distance < minDistanceTail)
	//			{
	//				minDistanceTail = contact.distance;
	//				nearestContactTail = contact;					
	//			}
	//		}			
	//	}
	//	Oni.Contact nearestContact = nearestContactAll;
	//	if (minDistanceTail < float.PositiveInfinity)
	//		nearestContact = nearestContactTail;

	//	if (isValidContact && IsHandOK(isLeft))
	//	{
	//		col = world.colliderHandles[nearestContact.bodyB].owner;
	//		var fingerTailCol = FindFingerTailCollider(col.gameObject);
	//		Debug.Log($"Collide with {col.gameObject.name}");

	//		if (isLeft)
	//		{
	//			isLeftHandHeld = true;
	//			Debug.Log("Attach to left hand!");
	//		}
	//		else
	//		{
	//			isRightHandHeld = true;
	//			Debug.Log("Attach to right hand!");
	//		}			

	//		// retrieve the offset and size of the simplex in the solver.simplices array:
	//		int simplexStart = solver.simplexCounts.GetSimplexStartAndSize(nearestContact.bodyA, out int simplexSize);

	//		// find the cloth actor that these particles belong to
	//		ObiSolver.ParticleInActor pa = solver.particleToActor[simplexStart];
	//		ObiActor actor = pa.actor as ObiActor;

	//		// Add current actor to heldActors list
	//		heldActors.Add(actor);

	//		// get a hold of the constraint type we want, in this case, pin constraints:
	//		var pinConstraints = actor.GetConstraintsByType(Oni.ConstraintType.Pin) as ObiConstraints<ObiPinConstraintsBatch>;

	//		// create a new pin constraints batch
	//		var batch = new ObiPinConstraintsBatch();
	//		// starting at simplexStart, iterate over all particles in the simplex:
	//		for (int i = 0; i < simplexSize; ++i)
	//		{
	//			int particleIndex = solver.simplices[simplexStart + i];
	//			// Add a couple constraints to it, pinning the nearest particles in the cloth:
	//			batch.AddConstraint(particleIndex, fingerTailCol, Vector3.zero, Quaternion.identity, 0, 0, float.PositiveInfinity);
	//		}
	//		Debug.Log("Add Pin constrains!");
	//		// set the amount of active constraints in the batch to simplexSize (the ones we just added).
	//		batch.activeConstraintCount += simplexSize;

	//		// append the batch to the pin constraints:
	//		pinConstraints.AddBatch(batch);

	//		// this will cause the solver to rebuild pin constraints at the beginning of the next frame:
	//		actor.SetConstraintsDirty(Oni.ConstraintType.Pin);
	//	}

	//}

}
