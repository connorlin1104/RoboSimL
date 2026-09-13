// The native half of DeviceStats.cs: what iOS knows about the phone that Unity doesn't pass on.
//
// Every function here must match a [DllImport("__Internal")] in DeviceStats.cs, name and type alike. Unity compiles a
// C# call to a name that isn't here without a word, and Xcode then fails to link with "Undefined symbol"; a type that
// doesn't match links and hands back garbage. Validate Performance Overlay compares the two files.
#import <Foundation/Foundation.h>
#include <mach/mach.h>
#include <os/proc.h>
#include <sys/sysctl.h>
#include <sys/time.h>
#include <unistd.h>

extern "C" {

// How hot the phone is, as iOS judges it: 0 nominal, 1 fair, 2 serious, 3 critical (NSProcessInfoThermalState). At
// serious and above iOS starts slowing the phone down to cool it.
int RoboSimThermalState(void)
{
    return (int)[[NSProcessInfo processInfo] thermalState];
}

// 1 when Low Power Mode is on, which caps the CPU and GPU and ProMotion's refresh rate — a run measured in it
// measures a slower phone.
int RoboSimLowPowerMode(void)
{
    return [[NSProcessInfo processInfo] isLowPowerModeEnabled] ? 1 : 0;
}

// The memory iOS counts against the app, in bytes: its physical footprint, the number iOS closes an app on. -1 when
// iOS won't say.
int64_t RoboSimFootprintBytes(void)
{
    task_vm_info_data_t info;
    mach_msg_type_number_t count = TASK_VM_INFO_COUNT;
    if (task_info(mach_task_self(), TASK_VM_INFO, (task_info_t)&info, &count) != KERN_SUCCESS) return -1;
    return (int64_t)info.phys_footprint;
}

// How much more memory the app may take before iOS closes it, in bytes.
int64_t RoboSimAvailableMemoryBytes(void)
{
    return (int64_t)os_proc_available_memory();
}

// Seconds since the process was created, which is about when the player tapped the icon. Unity's own clock only
// starts once the engine is up, so it misses the first part of every launch. -1 when iOS won't say.
double RoboSimSecondsSinceProcessStart(void)
{
    struct kinfo_proc process;
    size_t size = sizeof(process);
    int query[4] = { CTL_KERN, KERN_PROC, KERN_PROC_PID, getpid() };
    if (sysctl(query, 4, &process, &size, NULL, 0) != 0) return -1.0;
    struct timeval now;
    gettimeofday(&now, NULL);
    struct timeval started = process.kp_proc.p_starttime;
    return (double)(now.tv_sec - started.tv_sec) + (double)(now.tv_usec - started.tv_usec) / 1e6;
}

}
