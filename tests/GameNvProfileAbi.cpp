// Object-only assertions against unmodified MIT NVIDIA SDK headers at
// 70d337db9186e968eab622f7e786de7e437faf3d. Never linked or executed.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include "nvapi.h"
#include "NvApiDriverSettings.h"
#include <stddef.h>
static_assert(sizeof(void*) == 8 && sizeof(NvU32) == 4, "x64 NVAPI word layout");
static_assert(sizeof(NVDRS_APPLICATION_V4) == 20492, "V4 application size");
static_assert(offsetof(NVDRS_APPLICATION_V4, version) == 0, "application version");
static_assert(offsetof(NVDRS_APPLICATION_V4, isPredefined) == 4, "application provenance");
static_assert(offsetof(NVDRS_APPLICATION_V4, appName) == 8, "application name");
static_assert(offsetof(NVDRS_APPLICATION_V4, userFriendlyName) == 4104, "friendly name");
static_assert(offsetof(NVDRS_APPLICATION_V4, launcher) == 8200, "launcher selector");
static_assert(offsetof(NVDRS_APPLICATION_V4, fileInFolder) == 12296, "folder selector");
static_assert(offsetof(NVDRS_APPLICATION_V4, commandLine) == 16396, "command selector");
// The three SDK bitfields use one NvU32 word after fileInFolder. C++ does
// not permit offsetof on bitfields; assert the complete inter-field gap.
static_assert(offsetof(NVDRS_APPLICATION_V4, fileInFolder) + sizeof(NvAPI_UnicodeString) == 16392, "selector flags word start");
static_assert(offsetof(NVDRS_APPLICATION_V4, commandLine) - 16392 == sizeof(NvU32), "selector flags word width");
static_assert(NVDRS_APPLICATION_VER_V4 == 0x4500c, "V4 version tag");
static_assert(sizeof(NVDRS_PROFILE_V1) == 4116 && NVDRS_PROFILE_VER1 == 0x11014, "profile layout/version");
static_assert(offsetof(NVDRS_PROFILE_V1, numOfApps) == 4108 && offsetof(NVDRS_PROFILE_V1, numOfSettings) == 4112, "profile counts");
static_assert(sizeof(NVDRS_SETTING_V1) == 12320 && NVDRS_SETTING_VER1 == 0x13020, "setting layout/version");
static_assert(offsetof(NVDRS_SETTING_V1, settingLocation) == 4108 && offsetof(NVDRS_SETTING_V1, u32CurrentValue) == 8220, "setting provenance/value");
static_assert(NVAPI_SETTING_NOT_FOUND == -160 && NVAPI_PROFILE_NOT_FOUND == -163 && NVAPI_EXECUTABLE_NOT_FOUND == -166, "exact missing statuses");
static_assert(PREFERRED_PSTATE_ID == 0x1057EB71 && PREFERRED_PSTATE_PREFER_MAX == 1, "PState recipe");
static_assert(OGL_THREAD_CONTROL_ID == 0x20C1221E && OGL_THREAD_CONTROL_ENABLE == 1, "OpenGL recipe");
typedef NvAPI_Status (__cdecl *FindApplication)(NvDRSSessionHandle,NvAPI_UnicodeString,NvDRSProfileHandle*,NVDRS_APPLICATION*);
typedef NvAPI_Status (__cdecl *EnumApplications)(NvDRSSessionHandle,NvDRSProfileHandle,NvU32,NvU32*,NVDRS_APPLICATION*);
FindApplication GameNvFindPrototype = &NvAPI_DRS_FindApplicationByName;
EnumApplications GameNvEnumPrototype = &NvAPI_DRS_EnumApplications;
typedef NvAPI_Status (__cdecl *CreateProfile)(NvDRSSessionHandle,NVDRS_PROFILE*,NvDRSProfileHandle*);
typedef NvAPI_Status (__cdecl *CreateApplication)(NvDRSSessionHandle,NvDRSProfileHandle,NVDRS_APPLICATION*);
typedef NvAPI_Status (__cdecl *SetSetting)(NvDRSSessionHandle,NvDRSProfileHandle,NVDRS_SETTING*);
typedef NvAPI_Status (__cdecl *DeleteSetting)(NvDRSSessionHandle,NvDRSProfileHandle,NvU32);
typedef NvAPI_Status (__cdecl *DeleteApplication)(NvDRSSessionHandle,NvDRSProfileHandle,NvAPI_UnicodeString);
typedef NvAPI_Status (__cdecl *DeleteProfile)(NvDRSSessionHandle,NvDRSProfileHandle);
typedef NvAPI_Status (__cdecl *SessionOperation)(NvDRSSessionHandle);
typedef NvAPI_Status (__cdecl *EnumSettings)(NvDRSSessionHandle,NvDRSProfileHandle,NvU32,NvU32*,NVDRS_SETTING*);
CreateProfile GameNvCreateProfilePrototype = &NvAPI_DRS_CreateProfile;
CreateApplication GameNvCreateApplicationPrototype = &NvAPI_DRS_CreateApplication;
SetSetting GameNvSetPrototype = &NvAPI_DRS_SetSetting;
DeleteSetting GameNvDeleteSettingPrototype = &NvAPI_DRS_DeleteProfileSetting;
DeleteApplication GameNvUnbindPrototype = &NvAPI_DRS_DeleteApplication;
DeleteProfile GameNvDeleteProfilePrototype = &NvAPI_DRS_DeleteProfile;
SessionOperation GameNvSavePrototype = &NvAPI_DRS_SaveSettings;
SessionOperation GameNvLoadPrototype = &NvAPI_DRS_LoadSettings;
SessionOperation GameNvClosePrototype = &NvAPI_DRS_DestroySession;
EnumSettings GameNvEnumSettingsPrototype = &NvAPI_DRS_EnumSettings;
static_assert(NVAPI_NO_IMPLEMENTATION == -3 && NVAPI_INCOMPATIBLE_STRUCT_VERSION == -9 && NVAPI_INVALID_USER_PRIVILEGE == -137, "native capability errors are not absence");
static_assert(NVAPI_PROFILE_NAME_IN_USE == -164 && NVAPI_EXECUTABLE_ALREADY_IN_USE == -167 && NVAPI_PROFILE_REMOVED == -169 && NVAPI_EXECUTABLE_PATH_IS_AMBIGUOUS == -182 && NVAPI_END_ENUMERATION == -7, "exact create/scope/enumeration statuses");
