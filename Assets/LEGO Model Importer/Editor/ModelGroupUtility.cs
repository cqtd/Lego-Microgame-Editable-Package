﻿// Copyright (C) LEGO System A/S - All Rights Reserved
// Unauthorized copying of this file, via any medium is strictly prohibited

using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System.Linq;
using UnityEditor.SceneManagement;
using UnityEditor.Experimental.SceneManagement;

namespace LEGOModelImporter
{
    public class ModelGroupUtility
    {
        public enum UndoBehavior
        {
            withoutUndo,
            withUndo
        }

        public static Model CreateNewDefaultModel(string name, UndoBehavior undoBehavior = UndoBehavior.withUndo)
        {
            Model model = null;
            var modelGO = new GameObject();
            modelGO.name = name;
            model = modelGO.AddComponent<Model>();
            model.autoGenerated = true;
            model.pivot = Model.Pivot.BottomCenter;

            // Add LEGOModelAsset component.
            modelGO.AddComponent<LEGOModelAsset>();

            if(undoBehavior == UndoBehavior.withUndo)
            {
                Undo.RegisterCreatedObjectUndo(model.gameObject, "Creating model for brick without model");                            
            }
            return model;
        }

        public static ModelGroup CreateNewDefaultModelGroup(string name, UndoBehavior undoBehavior = UndoBehavior.withUndo)
        {
            ModelGroup group = null;
            var groupGO = new GameObject();
            group = groupGO.AddComponent<ModelGroup>();
            group.name = name;
            group.groupName = group.name;
            group.autoGenerated = true;

            // Add LEGOModelGroupAsset component.
            groupGO.AddComponent<LEGOModelGroupAsset>();

            if(undoBehavior == UndoBehavior.withUndo)
            {
                Undo.RegisterCreatedObjectUndo(group.gameObject, "Creating new model group");
            }            
            return group;
        }

        public static void RecomputePivot(Transform parent, Model.Pivot pivotType = Model.Pivot.BottomCenter, bool alignRotation = true, UndoBehavior undoBehavior = UndoBehavior.withUndo)
        {
            if(pivotType == Model.Pivot.Original)
            {
                return;
            }

            var bricks = parent.GetComponentsInChildren<Brick>();
            var referenceBrick = bricks.FirstOrDefault();
            if(!referenceBrick)
            {
                return;
            }

            var oldRotations = new List<Quaternion>();
            var oldPositions = new List<Vector3>();
            Matrix4x4 transformation = referenceBrick.transform.localToWorldMatrix.inverse;
            var bounds = BrickBuildingUtility.ComputeBounds(bricks, transformation);
            var pivot = bounds.center;

            if(pivotType == Model.Pivot.BottomCenter)
            {
                pivot += Vector3.down * bounds.extents.y;
            }

            pivot = referenceBrick.transform.TransformPoint(pivot);

            if(Vector3.Distance(parent.position, pivot) < float.Epsilon)
            {
                return;
            }

            if(undoBehavior == UndoBehavior.withUndo)
            {
                var collectedTransforms = new List<Transform>();
                collectedTransforms.Add(parent);
                for(var i = 0; i < parent.childCount; i++)
                {
                    var child = parent.GetChild(i);
                    collectedTransforms.Add(child);
                }
                Undo.RegisterCompleteObjectUndo(collectedTransforms.ToArray(), "Recording groups before moving model group");
            }

            var difference = parent.position - pivot;
            parent.position = pivot;            
            
            for(var i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                child.transform.position += difference;

                if(alignRotation)
                {
                    oldRotations.Add(child.transform.rotation);
                    oldPositions.Add(child.transform.position);
                }
            }

            if(alignRotation)
            {
                var rot = Quaternion.FromToRotation(parent.up, referenceBrick.transform.up);

                var forward = referenceBrick.transform.forward;
                var right = referenceBrick.transform.right;

                var oldRot = parent.rotation;
                parent.rotation = rot * parent.rotation;

                var m = Matrix4x4.TRS(parent.position, Quaternion.identity, Vector3.one);
                m.SetColumn(0, forward);
                m.SetColumn(1, parent.up);
                m.SetColumn(2, right);

                rot = MathUtils.AlignRotation(new Vector3[]{parent.right, parent.forward}, m) * rot;
                rot.ToAngleAxis(out float angle, out Vector3 axis);
                parent.rotation = oldRot;
                parent.RotateAround(parent.position, axis, angle);

                for(var i = 0; i < parent.childCount; i++)
                {
                    var child = parent.GetChild(i);
                    child.transform.rotation = oldRotations[i];
                    child.transform.position = oldPositions[i];
                }
            }
        }

        public static void RecomputePivot(Model model, bool alignRotation = true, UndoBehavior undoBehavior = UndoBehavior.withUndo)
        {
            //RecomputePivot(model.transform, model.pivot, alignRotation, undoBehavior);
        }

        public static void RecomputePivot(ModelGroup group, bool alignRotation = true, UndoBehavior undoBehavior = UndoBehavior.withUndo)
        {
            //RecomputePivot(group.transform, Model.Pivot.BottomCenter, alignRotation, undoBehavior);
        }

        private static void SetParent(Transform transform, Transform parent, UndoBehavior undoBehavior = UndoBehavior.withUndo)
        {
            if(undoBehavior == UndoBehavior.withUndo)
            {
                Undo.SetTransformParent(transform, parent, "Setting brick parent");
            }
            else
            {
                transform.SetParent(parent, true);
            }
        }

        private static bool IsPartOfPrefab(Brick brick)
        {
            return brick.transform.parent && PrefabUtility.IsPartOfAnyPrefab(brick.transform.parent) && !PrefabUtility.IsAddedGameObjectOverride(brick.gameObject);                
        }

        private static void UnpackPrefab(Brick brick, UndoBehavior undoBehavior = UndoBehavior.withUndo)
        {
            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(brick);
            PrefabUtility.UnpackPrefabInstance(root.gameObject, PrefabUnpackMode.Completely, undoBehavior == UndoBehavior.withUndo ? InteractionMode.UserAction : InteractionMode.AutomatedAction);
        }

        public static void RecomputeModelGroups(IEnumerable<Brick> bricks, bool alignRotation = true, UndoBehavior undoBehavior = UndoBehavior.withUndo)
        {
            // Group numbers are numbers in parentheses
            // So group 1 with three bricks is shown as (1, 1, 1) 
            // In case the group is part of a prefab it is suffixed with a p like (1p, 1p)
            // In case one of the bricks in a group is an override it is noted with a + as in (1p, 1p+)

            // Cases for splitting:
            // (1, 1) -> (1) (1)
            // (1, 1) (2) -> (1) (1, 2) -> (1) (2, 2)
            
            // Cases for unpacking:
            // (1p) (2p) -> (1, 2p) -> (2p+, 2p)
            // (1p, 1p+) (2p) -> (1, 1, 2p) -> (2p+, 2p+, 2p)
            // (1p, 1p) -> (1p) (1p) -> (1) (1)
            
            // Only set parent:
            // (1) (2) -> (1, 2) -> (2, 2)
            // (1p, 1p+) (2p) -> (1p) (1p+, 2p) -> (1p) (2p+, 2p)
            // (1p, 1p+) (2) -> (1p) (1p+, 2) -> (1p) (2, 2)

            if(PrefabStageUtility.GetCurrentPrefabStage() != null)
            {
                var rootObject = PrefabStageUtility.GetCurrentPrefabStage().prefabContentsRoot;
                var brick = rootObject.GetComponent<Brick>();
                if(brick)
                {
                    return;
                }
            }

            // First we flatten models
            var modelsToCheck = new HashSet<Model>();
            var groupsToCheck = new HashSet<ModelGroup>();
            var bricksToCheck = new HashSet<Brick>();

            foreach(var brick in bricks)
            {
                var bricksInParent = brick.GetComponentsInParent<Brick>();
                if(bricksInParent.Length > 1)
                {
                    bricksToCheck.Add(brick);
                }
                var modelsInParent = brick.GetComponentsInParent<Model>();
                if(modelsInParent.Length > 1)
                {
                    modelsToCheck.UnionWith(modelsInParent);
                }
                var groupsInParent = brick.GetComponentsInParent<ModelGroup>();
                if(groupsInParent.Length > 1)
                {
                    groupsToCheck.UnionWith(groupsInParent);
                }
            }

            foreach(var model in modelsToCheck)
            {
                var modelsInGroup = model.GetComponentsInChildren<Model>();
                foreach(var inGroup in modelsInGroup)
                {
                    if(inGroup == model)
                    {
                        continue;
                    }

                    var groupsInModel = inGroup.GetComponentsInChildren<ModelGroup>();
                    foreach(var group in groupsInModel)
                    {
                        SetParent(group.transform, model.transform, undoBehavior);
                    }
                }
            }

            // Now flatten groups
            foreach(var group in groupsToCheck)
            {
                var groupsInGroup = group.GetComponentsInChildren<ModelGroup>();
                foreach(var inGroup in groupsInGroup)
                {
                    if(inGroup == group)
                    {
                        continue;
                    }

                    var bricksInGroup = inGroup.GetComponentsInChildren<Brick>();
                    foreach(var brick in bricksInGroup)
                    {
                        SetParent(brick.transform, group.transform, undoBehavior);
                    }
                }
            }

            // Now flatten bricks
            foreach(var brick in bricksToCheck)
            {
                var group = brick.GetComponentInParent<ModelGroup>();
                if(group)
                {
                    SetParent(brick.transform, group.transform, undoBehavior);
                }
            }
            
            var connectedClusters = new List<HashSet<Brick>>();

            // Collect all connected brick lists
            foreach(var brick in bricks)
            {
                if(brick.parts.Count > 0 && !brick.parts[0].connectivity)
                {
                    continue;
                }

                if(connectedClusters.Any(x => x.Contains(brick)))
                {
                    continue;
                }

                var connected = brick.GetConnectedBricks();
                connected.Add(brick);
                connectedClusters.Add(connected);
            }

            // Now find all groups for each cluster
            var groupsPerCluster = new List<(HashSet<Brick>, HashSet<ModelGroup>, HashSet<Brick>)>();            
            foreach(var cluster in connectedClusters)
            {
                if(cluster.Count == 0)
                {
                    continue;
                }

                var bricksNotInGroup = new HashSet<Brick>();
                var groups = new HashSet<ModelGroup>();
                foreach(var brick in cluster)
                {
                    var group = brick.GetComponentInParent<ModelGroup>();                    
                    if(group)
                    {
                        groups.Add(group);
                    }
                    else
                    {
                        bricksNotInGroup.Add(brick);
                    }
                }
                groupsPerCluster.Add((cluster, groups, bricksNotInGroup));
            }

            // Sorting makes sure we merge before we split. Merging will make it easier to see what we need to split later.
            groupsPerCluster = groupsPerCluster.OrderByDescending(x => x.Item2.Count).ToList();

            // Check through each of these groups in the cluster
            foreach(var groupPerCluster in groupsPerCluster)
            {
                var cluster = groupPerCluster.Item1;
                var groups = groupPerCluster.Item2;
                var notInGroup = groupPerCluster.Item3;

                // If the cluster has more than one group, we need to merge them
                if(groups.Count > 1)
                {
                    // Merge some groups
                    ModelGroup largestGroup = null;
                    int largestGroupSize = 0;
                    foreach(var group in groups)
                    {
                        var bricksInGroup = group.GetComponentsInChildren<Brick>();
                        var bricksInCluster = bricksInGroup.Count(x => cluster.Contains(x));
                        if(bricksInCluster >= largestGroupSize)
                        {
                            largestGroup = group;
                            largestGroupSize = bricksInCluster;
                        }
                    }
                    
                    foreach(var brick in cluster)
                    {
                        if(brick.transform.parent == largestGroup.transform)
                        {
                            continue;
                        }

                        if(IsPartOfPrefab(brick))
                        {
                            UnpackPrefab(brick, undoBehavior);
                        }
                        SetParent(brick.transform, largestGroup.transform, undoBehavior);
                    }

                    RecomputePivot(largestGroup, alignRotation, undoBehavior);
                    var modelGO = largestGroup.transform.parent;
                    var model = modelGO.GetComponent<Model>();
                    if(model)
                    {
                        RecomputePivot(model, alignRotation, undoBehavior);
                    }
                }
                else if(groups.Count == 1) // In case the cluster only has one group, we need to check if the group contains bricks not in this cluster
                {                    
                    var group = groups.FirstOrDefault();
                    if(!group)
                    {
                        continue;
                    }

                    var bricksInGroup = group.GetComponentsInChildren<Brick>();
                    var clustersForGroup = new HashSet<HashSet<Brick>>();

                    // If this group contains more than one cluster, split
                    foreach(var brick in bricksInGroup)
                    {
                        if(!clustersForGroup.Any(x => x.Contains(brick)))
                        {
                            var connected = brick.GetConnectedBricks();
                            connected.Add(brick);
                            clustersForGroup.Add(connected);
                        }
                    }

                    if(clustersForGroup.Count > 1)
                    {
                        // Get the model for the group
                        var model = group.GetComponentInParent<Model>();

                        // Find all prefabs we need to unpack by looking through the clusters in the group
                        foreach(var clusterInGroup in clustersForGroup)
                        {
                            // Look through each brick in the cluster
                            foreach(var brick in clusterInGroup)
                            {
                                // First check if there is a brick in this cluster that is part of a prefab and not an override
                                // If there is, then check if there is another cluster containing a prefab that is not an override
                                // In that case, we have to unpack, because we are changing the parents of gameobjects in a prefab
                                if(IsPartOfPrefab(brick))
                                {
                                    if(clustersForGroup.Any(clust => clust != clusterInGroup && clust.Any(obj => !PrefabUtility.IsAddedGameObjectOverride(obj.gameObject))))
                                    {
                                        UnpackPrefab(brick, undoBehavior);
                                        break;
                                    }
                                }
                            }
                        }

                        
                        HashSet<Brick> largestGroupBricks = null;
                        ModelGroup largestGroup = null;
                        int largestGroupSize = 0;
                        var sharedParentClusters = new HashSet<ModelGroup>();

                        foreach(var clusterInGroup in clustersForGroup)
                        {
                            var parent = clusterInGroup.First().transform.parent;
                            var modelGroup = parent.GetComponent<ModelGroup>();
                            if(!modelGroup)
                            {
                                continue;
                            }

                            var skip = false;
                            foreach(var brick in clusterInGroup)
                            {
                                if(brick.transform.parent != parent)
                                {
                                    skip = true;
                                    break;
                                }
                            }
                            if(skip)
                            {
                                continue;
                            }

                            if(largestGroupSize < clusterInGroup.Count())
                            {
                                largestGroupSize = clusterInGroup.Count();
                                largestGroupBricks = clusterInGroup;
                                largestGroup = modelGroup;
                            }
                        }

                        if(largestGroupBricks != null)
                        {
                            clustersForGroup.Remove(largestGroupBricks);
                            RecomputePivot(largestGroup, alignRotation, undoBehavior);
                        }

                        foreach(var clusterInGroup in clustersForGroup)
                        {
                            var newObject = new GameObject();
                            var newGroup = newObject.AddComponent<ModelGroup>();

                            // Add LEGOModelGroupAsset component.
                            newObject.AddComponent<LEGOModelGroupAsset>();

                            if(undoBehavior == UndoBehavior.withUndo)
                            {
                                Undo.RegisterCreatedObjectUndo(newObject, "Created new group");
                            }
                            
                            newGroup.transform.position = group.transform.position;

                            if(model)
                            {
                                SetParent(newGroup.transform, model.transform, undoBehavior);
                            }
                            
                            newGroup.name = group.groupName;
                            newGroup.groupName = group.groupName;
                            newGroup.parentName = group.parentName;
                            newGroup.optimizations = group.optimizations;
                            newGroup.randomizeNormals = group.randomizeNormals;
                            foreach(var view in group.views)
                            {
                                newGroup.views.Add(new CullingCameraConfig()
                                {
                                    name = view.name,
                                    perspective = view.perspective,
                                    position = view.position,
                                    rotation = view.rotation,
                                    fov = view.fov,
                                    size = view.size,
                                    minRange = view.minRange,
                                    maxRange = view.maxRange,
                                    aspect = view.aspect
                                });
                            }
                            newGroup.autoGenerated = true;

                            foreach(var brick in clusterInGroup)
                            {
                                SetParent(brick.transform, newGroup.transform, undoBehavior);
                            }
                            RecomputePivot(newGroup, alignRotation, undoBehavior);
                        }

                        if(model)
                        {
                            RecomputePivot(model, alignRotation, undoBehavior);
                        }
                    }
                    else if(notInGroup.Count > 0)
                    {
                        foreach(var brick in notInGroup)
                        {
                            if(IsPartOfPrefab(brick))
                            {
                                UnpackPrefab(brick, undoBehavior);
                            }
                            SetParent(brick.transform, group.transform);
                        }
                    }
                    else
                    {
                        RecomputePivot(group, alignRotation, undoBehavior);
                        var modelGO = group.transform.parent;
                        Model model = null;
                        bool createNewModel = PrefabStageUtility.GetCurrentPrefabStage() == null && (!modelGO || !modelGO.GetComponent<Model>());
                        if(createNewModel)
                        {
                            model = CreateNewDefaultModel(group.name, undoBehavior);
                            SetParent(group.transform, model.transform, undoBehavior);
                            EditorGUIUtility.PingObject(group.gameObject);
                        }
                        else
                        {
                            model = modelGO.GetComponent<Model>();
                        }

                        if(model)
                        {
                            RecomputePivot(model, alignRotation, undoBehavior);
                        }
                    }
                }
                else
                {
                    var name = cluster.FirstOrDefault()?.name;
                    Model model = null;

                    if(PrefabStageUtility.GetCurrentPrefabStage() != null)
                    {
                        var rootObject = PrefabStageUtility.GetCurrentPrefabStage().prefabContentsRoot;
                        model = rootObject.GetComponent<Model>();
                        if(!model)
                        {
                            model = CreateNewDefaultModel(name);
                            SetParent(model.transform, rootObject.transform, undoBehavior);
                        }
                    }
                    else
                    {
                        model = CreateNewDefaultModel(name);
                    }

                    ModelGroup newGroup = CreateNewDefaultModelGroup(name);

                    SetParent(newGroup.transform, model.transform, undoBehavior);
                    var bounds = BrickBuildingUtility.ComputeBounds(cluster, Matrix4x4.identity);
                    model.transform.position = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);

                    Transform originalParent = null;
                    foreach(var brick in cluster)
                    {
                        if(!originalParent)
                        {
                            originalParent = brick.transform.parent;
                        }
                        if(brick.transform.parent != originalParent)
                        {
                            originalParent = null;
                            break;
                        }
                    }

                    if(originalParent)
                    {
                        SetParent(model.transform, originalParent, undoBehavior);
                    }

                    foreach(var brick in cluster)
                    {
                        SetParent(brick.transform, newGroup.transform, undoBehavior);
                        EditorGUIUtility.PingObject(brick.gameObject);
                    }
                }
            }

            var modelsInScene = StageUtility.GetCurrentStageHandle().FindComponentsOfType<Model>();
            foreach(var model in modelsInScene)
            {
                var children = model.GetComponentsInChildren<Brick>();
                if(children.Length == 0)
                {
                    if (undoBehavior == UndoBehavior.withUndo)
                    {
                        Undo.DestroyObjectImmediate(model.gameObject);
                    }
                    else
                    {
                        Object.DestroyImmediate(model.gameObject);
                    }
                }
            }

            var groupsInScene = StageUtility.GetCurrentStageHandle().FindComponentsOfType<ModelGroup>();
            foreach(var group in groupsInScene)
            {
                var children = group.GetComponentsInChildren<Brick>();
                if(children.Length == 0)
                {
                    if (undoBehavior == UndoBehavior.withUndo)
                    {
                        Undo.DestroyObjectImmediate(group.gameObject);
                    }
                    else
                    {
                        Object.DestroyImmediate(group.gameObject);
                    }
                }
            }            
        }      
    }
}