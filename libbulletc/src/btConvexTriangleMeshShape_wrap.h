#include "main.h"

extern "C"
{
	EXPORT btConvexTriangleMeshShape* btConvexTriangleMeshShape_new(btStridingMeshInterface* meshInterface);
	EXPORT btConvexTriangleMeshShape* btConvexTriangleMeshShape_new2(btStridingMeshInterface* meshInterface, bool calcAabb);
	EXPORT void btConvexTriangleMeshShape_calculatePrincipalAxisTransform(btConvexTriangleMeshShape* obj, btTransform* principal, btVector3* inertia, btScalar* volume);
	EXPORT btStridingMeshInterface* btConvexTriangleMeshShape_getMeshInterface(btConvexTriangleMeshShape* obj);
}
