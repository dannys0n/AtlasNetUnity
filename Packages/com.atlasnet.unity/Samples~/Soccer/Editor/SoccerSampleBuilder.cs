#if UNITY_EDITOR
using System.IO;
using AtlasNet;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AtlasNet.Samples.Soccer.Editor
{
    public static class SoccerSampleBuilder
    {
        [MenuItem("AtlasNet/Samples/Create Soccer Scene")]
        public static void Build()
        {
            if (EditorApplication.isPlaying) throw new System.InvalidOperationException("Leave Play Mode before rebuilding Soccer.");
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            string script=AssetDatabase.GUIDToAssetPath(AssetDatabase.FindAssets("SoccerSampleBuilder t:MonoScript")[0]);
            string root=Path.GetDirectoryName(Path.GetDirectoryName(script)).Replace('\\','/');
            Directory.CreateDirectory(root+"/Prefabs"); Directory.CreateDirectory(root+"/Materials"); AssetDatabase.Refresh();
            string source=null;
            foreach(var guid in AssetDatabase.FindAssets("AnimatorPlayer t:Prefab"))
            {
                string path=AssetDatabase.GUIDToAssetPath(guid);
                if(path.EndsWith("/Prefabs/AnimatorPlayer.prefab")){source=path;break;}
            }
            if(source==null)throw new System.InvalidOperationException("Import the 00 Shared sample first.");
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var green=Material(root,"Pitch",new Color(.055f,.27f,.15f));
            var stripe=Material(root,"Stripe",new Color(.065f,.31f,.18f));
            var white=Material(root,"White",new Color(.96f,.98f,1));
            var dark=Material(root,"Border",new Color(.035f,.055f,.075f));
            var blue=Material(root,"Blue",SoccerSession.Blue);var orange=Material(root,"Orange",SoccerSession.Orange);
            Box("Pitch 105 x 68 m",new Vector3(0,-.3f,0),new Vector3(68,.6f,105),green);
            for(int i=0;i<10;i++) if(i%2==0)Box("Mown stripe",new Vector3(0,.002f,-47.25f+i*10.5f),new Vector3(68,.004f,10.5f),stripe,false);
            Box("West board",new Vector3(-34.5f,1,0),new Vector3(1,2,107),dark);
            Box("East board",new Vector3(34.5f,1,0),new Vector3(1,2,107),dark);
            Box("Blue end board",new Vector3(0,3,-53),new Vector3(70,6,1),dark);
            Box("Orange end board",new Vector3(0,3,53),new Vector3(70,6,1),dark);
            for(int side=-1;side<=1;side+=2)
            {
                var team=side<0?blue:orange;
                Box("Flush goal face",new Vector3(0,SoccerBall.GoalHeight*.5f,side*52.43f),new Vector3(12,SoccerBall.GoalHeight,.12f),team);
                Line("Goal line",new[]{new Vector3(-6,.025f,side*51.7f),new Vector3(6,.025f,side*51.7f)},white,.16f);
                Line("Penalty area",new[]{new Vector3(-20,.025f,side*52),new Vector3(-20,.025f,side*36),new Vector3(20,.025f,side*36),new Vector3(20,.025f,side*52)},white,.12f);
            }
            Line("Touchlines",new[]{new Vector3(-33,.025f,-52),new Vector3(-33,.025f,52),new Vector3(33,.025f,52),new Vector3(33,.025f,-52),new Vector3(-33,.025f,-52)},white,.12f);
            Line("Halfway",new[]{new Vector3(-33,.025f,0),new Vector3(33,.025f,0)},white,.12f);
            var circle=new Vector3[65];for(int i=0;i<circle.Length;i++){float a=i*2*Mathf.PI/64;circle[i]=new Vector3(Mathf.Cos(a)*9.15f,.025f,Mathf.Sin(a)*9.15f);}Line("Centre circle",circle,white,.12f);
            var cage=new GameObject("Invisible safety cage");
            CageBox(cage.transform,"Left end",new Vector3(-52.55f,4,0),new Vector3(.4f,8,67.8f));
            CageBox(cage.transform,"Right end",new Vector3(52.55f,4,0),new Vector3(.4f,8,67.8f));
            CageBox(cage.transform,"North side",new Vector3(0,4,33.7f),new Vector3(105.5f,8,.4f));
            CageBox(cage.transform,"South side",new Vector3(0,4,-33.7f),new Vector3(105.5f,8,.4f));
            CageBox(cage.transform,"Ceiling",new Vector3(0,8.25f,0),new Vector3(105.5f,.5f,67.8f));
            var player=(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(source));player.name="SoccerPlayer";player.transform.localScale=Vector3.one*SoccerPlayer.Size;
            foreach(var c in player.GetComponents<MonoBehaviour>())
                if(c is SamplePlanarMovement || c is SampleAnimatorInput || c is SimpleRpcExample) Object.DestroyImmediate(c);
            Set(player.GetComponent<NetworkObject>(),"prefabId","atlasnet.soccer.player");
            var nt=player.GetComponent<NetworkTransform>();Set(nt,"positionWriter",0);Set(nt,"rotationWriter",0);
            Set(player.GetComponent<NetworkAnimator>(),"writer",0);
            var rb=player.AddComponent<Rigidbody>();rb.isKinematic=false;rb.useGravity=false;rb.mass=80;rb.interpolation=RigidbodyInterpolation.Interpolate;rb.collisionDetectionMode=CollisionDetectionMode.ContinuousDynamic;rb.constraints=RigidbodyConstraints.FreezePositionY|RigidbodyConstraints.FreezeRotation;
            var capsule=player.AddComponent<CapsuleCollider>();capsule.height=2.1f;capsule.radius=.96f*SoccerBall.Radius/SoccerPlayer.Size;capsule.center=new Vector3(0,1.05f,0);capsule.isTrigger=false;
            // Solid server-simulated characters stop the ball; moving contacts add one kick.
            player.AddComponent<SoccerPlayer>();
            var interest=player.AddComponent<NetworkInterestSource>();Set(interest,"radius",160f);
            string sharedRoot=Path.GetDirectoryName(Path.GetDirectoryName(source)).Replace('\\','/');
            var interestOverlay=player.AddComponent<ScaleInterestOverlay>();Set(interestOverlay,"player",player.GetComponent<NetworkObject>());Set(interestOverlay,"interest",interest);Set(interestOverlay,"fillMaterial",AssetDatabase.LoadAssetAtPath<Material>(sharedRoot+"/Materials/ScaleInterest.mat"));Set(interestOverlay,"showOnAuthorityServer",true);
            var playerIndicator=player.AddComponent<SoccerNetworkIndicator>();playerIndicator.bodyRenderer=System.Array.Find(player.GetComponentsInChildren<Renderer>(),r=>r.name=="Torso");playerIndicator.authorityMaterial=AssetDatabase.LoadAssetAtPath<Material>(sharedRoot+"/Materials/ScaleAuthority.mat");playerIndicator.ghostMaterial=AssetDatabase.LoadAssetAtPath<Material>(sharedRoot+"/Materials/ScaleGhost.mat");
            player.GetComponent<Animator>().runtimeAnimatorController=Controller(root,player.GetComponent<Animator>().runtimeAnimatorController);
            var playerPrefab=PrefabUtility.SaveAsPrefabAsset(player,root+"/Prefabs/SoccerPlayer.prefab").GetComponent<NetworkObject>();Object.DestroyImmediate(player);
            var ball=GameObject.CreatePrimitive(PrimitiveType.Sphere);ball.name="SoccerBall";ball.transform.localScale=Vector3.one*(SoccerBall.Radius*2);ball.GetComponent<Renderer>().sharedMaterial=white;
            string bouncePath=root+"/Materials/BallBounce.physicMaterial";var bounce=AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(bouncePath);if(!bounce){bounce=new PhysicsMaterial("BallBounce"){bounciness=.8f,dynamicFriction=0,staticFriction=0,bounceCombine=PhysicsMaterialCombine.Maximum};AssetDatabase.CreateAsset(bounce,bouncePath);}bounce.staticFriction=0;bounce.dynamicFriction=0;bounce.frictionCombine=PhysicsMaterialCombine.Minimum;EditorUtility.SetDirty(bounce);ball.GetComponent<Collider>().sharedMaterial=bounce;
            var ballObject=ball.AddComponent<NetworkObject>();Set(ballObject,"prefabId","atlasnet.soccer.ball");
            nt=ball.AddComponent<NetworkTransform>();Set(nt,"rotationWriter",0);
            rb=ball.AddComponent<Rigidbody>();rb.useGravity=true;rb.mass=.45f;rb.linearDamping=1.5f;rb.interpolation=RigidbodyInterpolation.Interpolate;rb.collisionDetectionMode=CollisionDetectionMode.ContinuousDynamic;rb.constraints=RigidbodyConstraints.FreezeRotation;
            ball.AddComponent<NetworkRigidbody>();ball.AddComponent<SoccerMatch>();ball.AddComponent<SoccerBall>();
            var ballIndicator=ball.AddComponent<SoccerNetworkIndicator>();ballIndicator.bodyRenderer=ball.GetComponent<Renderer>();ballIndicator.authorityMaterial=AssetDatabase.LoadAssetAtPath<Material>(sharedRoot+"/Materials/ScaleAuthority.mat");ballIndicator.ghostMaterial=AssetDatabase.LoadAssetAtPath<Material>(sharedRoot+"/Materials/ScaleGhost.mat");
            var ballPrefab=PrefabUtility.SaveAsPrefabAsset(ball,root+"/Prefabs/SoccerBall.prefab").GetComponent<NetworkObject>();Object.DestroyImmediate(ball);
            string registryPath=root+"/Prefabs/SoccerNetworkPrefabs.asset";
            var registry=AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(registryPath);if(!registry){registry=ScriptableObject.CreateInstance<NetworkPrefabsList>();AssetDatabase.CreateAsset(registry,registryPath);}
            var serialized=new SerializedObject(registry);var list=serialized.FindProperty("prefabs");list.arraySize=2;list.GetArrayElementAtIndex(0).objectReferenceValue=playerPrefab;list.GetArrayElementAtIndex(1).objectReferenceValue=ballPrefab;serialized.ApplyModifiedPropertiesWithoutUndo();
            var managerObject=new GameObject("Soccer NetworkManager");var manager=managerObject.AddComponent<NetworkManager>();Set(manager,"playerPrefab",playerPrefab);Set(manager,"automaticLocalHandoffs",true);Set(manager,"localPartitionCenter",managerObject.transform);
            serialized=new SerializedObject(manager);list=serialized.FindProperty("networkPrefabsLists");list.arraySize=1;list.GetArrayElementAtIndex(0).objectReferenceValue=registry;serialized.ApplyModifiedPropertiesWithoutUndo();
            var session=managerObject.AddComponent<SoccerSession>();session.manager=manager;session.ballPrefab=ballPrefab;session.webBaseUrl="http://localhost:8094/";
            session.font=AssetDatabase.LoadAssetAtPath<TMPro.TMP_FontAsset>("Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset");
            if(!session.font)throw new System.InvalidOperationException("Import TMP Essential Resources before building Soccer.");
            var region=new GameObject("Worker debug region",typeof(MeshFilter),typeof(MeshRenderer));var regionRenderer=region.GetComponent<MeshRenderer>();regionRenderer.sharedMaterial=AssetDatabase.LoadAssetAtPath<Material>(sharedRoot+"/Materials/ScaleRegion.mat");regionRenderer.enabled=false;
            var regionOverlay=managerObject.AddComponent<ScaleRegionOverlay>();Set(regionOverlay,"manager",manager);Set(regionOverlay,"regionMeshFilter",region.GetComponent<MeshFilter>());Set(regionOverlay,"regionRenderer",regionRenderer);
            var boundaries=managerObject.AddComponent<ClientBoundaryOverlay>();Set(boundaries,"manager",manager);Set(boundaries,"wallMaterial",regionRenderer.sharedMaterial);Set(boundaries,"showBoundaries",true);Set(boundaries,"viewRadius",180f);
            var pointer=managerObject.AddComponent<SoccerGoalPointer>();pointer.manager=manager;pointer.arrowMesh=ArrowMesh(root);
            pointer.arrowMaterial=Material(root,"GoalPointer",Color.white);pointer.arrowMaterial.shader=Shader.Find("Universal Render Pipeline/Unlit")??Shader.Find("Unlit/Color");
            var cameraObject=new GameObject("Soccer Camera",typeof(Camera),typeof(AudioListener));cameraObject.tag="MainCamera";cameraObject.transform.position=new Vector3(0,85,-70);cameraObject.transform.rotation=Quaternion.Euler(49,0,0);var camera=cameraObject.GetComponent<Camera>();camera.clearFlags=CameraClearFlags.SolidColor;camera.fieldOfView=54;camera.farClipPlane=300;camera.backgroundColor=new Color(.02f,.045f,.06f);
            var light=new GameObject("Sun",typeof(Light));light.GetComponent<Light>().type=LightType.Directional;light.GetComponent<Light>().intensity=1.5f;light.transform.rotation=Quaternion.Euler(55,-25,0);light.GetComponent<Light>().shadows=LightShadows.Soft;
            RenderSettings.ambientLight=new Color(.65f,.7f,.8f);RenderSettings.ambientMode=UnityEngine.Rendering.AmbientMode.Flat;
            AssetDatabase.SaveAssets();EditorSceneManager.SaveScene(scene,root+"/08_Soccer.unity");
        }
        static void CageBox(Transform parent,string name,Vector3 center,Vector3 size)
        {
            var obj=new GameObject(name,typeof(BoxCollider));obj.transform.SetParent(parent,false);obj.transform.position=center;obj.GetComponent<BoxCollider>().size=size;
        }
        static Mesh ArrowMesh(string root)
        {
            string path=root+"/Prefabs/GoalPointerMesh.asset";var mesh=AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if(mesh)return mesh;
            mesh=new Mesh{name="Goal direction arrow"};
            mesh.vertices=new[]{new Vector3(-.65f,0,2.3f),new Vector3(.65f,0,2.3f),new Vector3(0,0,3.4f),new Vector3(-.2f,0,1.5f),new Vector3(.2f,0,1.5f),new Vector3(-.2f,0,2.4f),new Vector3(.2f,0,2.4f)};
            mesh.triangles=new[]{0,2,1,3,5,4,4,5,6};mesh.RecalculateNormals();mesh.RecalculateBounds();AssetDatabase.CreateAsset(mesh,path);return mesh;
        }
        static RuntimeAnimatorController Controller(string root,RuntimeAnimatorController source)
        {
            string folder=root+"/Animations";Directory.CreateDirectory(folder);AssetDatabase.Refresh();
            string sourceRoot=Path.GetDirectoryName(AssetDatabase.GetAssetPath(source));
            string path=folder+"/SoccerRobot.controller";
            var existing=AssetDatabase.LoadAssetAtPath<AnimatorController>(path);if(existing)return existing;
            var controller=AnimatorController.CreateAnimatorControllerAtPath(path);controller.AddParameter("Speed",AnimatorControllerParameterType.Float);controller.AddParameter("Wave",AnimatorControllerParameterType.Trigger);
            var machine=controller.layers[0].stateMachine;
            var idle=machine.AddState("Idle");idle.motion=AssetDatabase.LoadAssetAtPath<AnimationClip>(sourceRoot+"/RobotIdle.anim");
            var walk=machine.AddState("Walk");walk.motion=AssetDatabase.LoadAssetAtPath<AnimationClip>(sourceRoot+"/RobotWalk.anim");machine.defaultState=idle;
            var transition=idle.AddTransition(walk);transition.hasExitTime=false;transition.duration=.12f;transition.AddCondition(AnimatorConditionMode.Greater,.1f,"Speed");
            transition=walk.AddTransition(idle);transition.hasExitTime=false;transition.duration=.12f;transition.AddCondition(AnimatorConditionMode.Less,.1f,"Speed");
            var wave=Object.Instantiate(AssetDatabase.LoadAssetAtPath<AnimationClip>(sourceRoot+"/RobotWave.anim"));wave.name="SoccerWaveArm";
            foreach(var binding in AnimationUtility.GetCurveBindings(wave))if(!binding.path.StartsWith("Body/RightArm"))AnimationUtility.SetEditorCurve(wave,binding,null);
            AssetDatabase.CreateAsset(wave,folder+"/SoccerWaveArm.anim");
            var mask=new AvatarMask();mask.name="Wave right arm";mask.transformCount=3;mask.SetTransformPath(0,"");mask.SetTransformActive(0,false);mask.SetTransformPath(1,"Body");mask.SetTransformActive(1,false);mask.SetTransformPath(2,"Body/RightArm");mask.SetTransformActive(2,true);AssetDatabase.CreateAsset(mask,folder+"/WaveArm.mask");
            var upper=new AnimatorStateMachine{name="Wave overlay"};AssetDatabase.AddObjectToAsset(upper,controller);
            var rest=upper.AddState("Rest");rest.writeDefaultValues=false;upper.defaultState=rest;var waving=upper.AddState("Wave");waving.motion=wave;waving.writeDefaultValues=false;
            transition=rest.AddTransition(waving);transition.hasExitTime=false;transition.duration=.12f;transition.AddCondition(AnimatorConditionMode.If,0,"Wave");
            transition=waving.AddTransition(rest);transition.hasExitTime=true;transition.exitTime=1;transition.duration=.15f;
            controller.AddLayer(new AnimatorControllerLayer{name="Wave overlay",stateMachine=upper,avatarMask=mask,defaultWeight=1,blendingMode=AnimatorLayerBlendingMode.Override});return controller;
        }
        static void Set(Object obj,string field,float value){var so=new SerializedObject(obj);so.FindProperty(field).floatValue=value;so.ApplyModifiedPropertiesWithoutUndo();}
        static Material Material(string root,string name,Color color)
        {
            string path=root+"/Materials/"+name+".mat";var mat=AssetDatabase.LoadAssetAtPath<Material>(path);
            if(!mat){mat=new Material(Shader.Find("Universal Render Pipeline/Lit")??Shader.Find("Standard"));AssetDatabase.CreateAsset(mat,path);}mat.color=color;EditorUtility.SetDirty(mat);return mat;
        }
        static GameObject Box(string name,Vector3 pos,Vector3 size,Material mat,bool collider=true)
        {var obj=GameObject.CreatePrimitive(PrimitiveType.Cube);obj.name=name;obj.transform.position=new Vector3(pos.z,pos.y,-pos.x);obj.transform.localScale=new Vector3(size.z,size.y,size.x);obj.GetComponent<Renderer>().sharedMaterial=mat;if(!collider)Object.DestroyImmediate(obj.GetComponent<Collider>());return obj;}
        static void Line(string name,Vector3[] points,Material mat,float width)
        {var obj=new GameObject(name);var line=obj.AddComponent<LineRenderer>();line.sharedMaterial=mat;line.positionCount=points.Length;for(int i=0;i<points.Length;i++)points[i]=new Vector3(points[i].z,points[i].y,-points[i].x);line.SetPositions(points);line.startWidth=line.endWidth=width;line.alignment=LineAlignment.TransformZ;obj.transform.rotation=Quaternion.Euler(90,0,0);line.useWorldSpace=true;}
        static void Set(Object obj,string field,Object value){var so=new SerializedObject(obj);so.FindProperty(field).objectReferenceValue=value;so.ApplyModifiedPropertiesWithoutUndo();}
        static void Set(Object obj,string field,int value){var so=new SerializedObject(obj);so.FindProperty(field).intValue=value;so.ApplyModifiedPropertiesWithoutUndo();}
        static void Set(Object obj,string field,bool value){var so=new SerializedObject(obj);so.FindProperty(field).boolValue=value;so.ApplyModifiedPropertiesWithoutUndo();}
        static void Set(Object obj,string field,string value){var so=new SerializedObject(obj);so.FindProperty(field).stringValue=value;so.ApplyModifiedPropertiesWithoutUndo();}
    }
}
#endif
