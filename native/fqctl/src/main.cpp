#include <cerrno>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <string_view>

#include <ftw.h>
#include <sys/mount.h>
#include <sys/stat.h>
#include <unistd.h>

namespace {

constexpr char kVersion[] = "0.1.0";

enum class ExitCode : int {
  Ok = 0,
  Usage = 1,
  NotFound = 2,
  Permission = 3,
  Busy = 4,
  OsError = 5,
};

ExitCode FromErrno(int err)
{
  switch (err) {
  case ENOENT:
  case ENOTDIR:
  case ELOOP:
    return ExitCode::NotFound;
  case EACCES:
  case EPERM:
  case EROFS:
    return ExitCode::Permission;
  case EBUSY:
    return ExitCode::Busy;
  default:
    return ExitCode::OsError;
  }
}

void PrintErrno(const char* op, const char* path, int err)
{
  std::fprintf(stderr, "fqctl: %s %s: %s\n", op, path, std::strerror(err));
}

void Usage()
{
  std::fprintf(stderr,
               "usage:\n"
               "  fqctl umount-lazy <path>\n"
               "  fqctl chown-r <uid:gid> <path>\n"
               "  fqctl mount-bind <source> <target>\n"
               "  fqctl version\n"
               "\n"
               "exit codes: 0 ok, 1 usage, 2 not found, 3 permission, 4 busy, 5 os error\n");
}

bool ParseUidGid(std::string_view spec, uid_t* uid, gid_t* gid)
{
  const auto colon = spec.find(':');
  if (colon == std::string_view::npos || colon == 0 || colon + 1 >= spec.size())
    return false;

  const auto uid_part = spec.substr(0, colon);
  const auto gid_part = spec.substr(colon + 1);
  if (uid_part.find_first_not_of("0123456789") != std::string_view::npos)
    return false;
  if (gid_part.find_first_not_of("0123456789") != std::string_view::npos)
    return false;

  char* end = nullptr;
  const unsigned long u = std::strtoul(std::string(uid_part).c_str(), &end, 10);
  if (end == nullptr || *end != '\0' || u > static_cast<unsigned long>(static_cast<uid_t>(-1)))
    return false;
  end = nullptr;
  const unsigned long g = std::strtoul(std::string(gid_part).c_str(), &end, 10);
  if (end == nullptr || *end != '\0' || g > static_cast<unsigned long>(static_cast<gid_t>(-1)))
    return false;

  *uid = static_cast<uid_t>(u);
  *gid = static_cast<gid_t>(g);
  return true;
}

struct ChownState {
  uid_t uid = 0;
  gid_t gid = 0;
  int err = 0;
  const char* path = nullptr;
};

ChownState* g_chown = nullptr;

int ChownWalk(const char* fpath, const struct stat* /*sb*/, int /*typeflag*/, struct FTW* /*ftwbuf*/)
{
  if (lchown(fpath, g_chown->uid, g_chown->gid) != 0) {
    g_chown->err = errno;
    g_chown->path = fpath;
    return 1;
  }
  return 0;
}

ExitCode CmdUmountLazy(const char* path)
{
  if (umount2(path, MNT_DETACH) == 0)
    return ExitCode::Ok;

  const int err = errno;
  // Already unmounted is success for stale-mounts IN THEORY now tbh idk xD
  if (err == EINVAL || err == ENOENT)
    return ExitCode::Ok;

  PrintErrno("umount-lazy", path, err);
  return FromErrno(err);
}

ExitCode CmdChownR(const char* owner, const char* path)
{
  uid_t uid = 0;
  gid_t gid = 0;
  if (!ParseUidGid(owner, &uid, &gid)) {
    std::fprintf(stderr, "fqctl: chown-r expects numeric uid:gid, got '%s'\n", owner);
    return ExitCode::Usage;
  }

  struct stat st {};
  if (lstat(path, &st) != 0) {
    const int err = errno;
    PrintErrno("chown-r", path, err);
    return FromErrno(err);
  }

  ChownState state{.uid = uid, .gid = gid};
  g_chown = &state;
  const int walk = nftw(path, ChownWalk, 64, FTW_PHYS | FTW_DEPTH);
  g_chown = nullptr;

  if (walk != 0) {
    const int err = state.err != 0 ? state.err : errno;
    PrintErrno("chown-r", state.path != nullptr ? state.path : path, err);
    return FromErrno(err);
  }

  return ExitCode::Ok;
}

ExitCode CmdMountBind(const char* source, const char* target)
{
  struct stat st {};
  if (stat(source, &st) != 0) {
    const int err = errno;
    PrintErrno("mount-bind source", source, err);
    return FromErrno(err);
  }
  if (stat(target, &st) != 0) {
    const int err = errno;
    PrintErrno("mount-bind target", target, err);
    return FromErrno(err);
  }

  if (mount(source, target, nullptr, MS_BIND, nullptr) == 0)
    return ExitCode::Ok;

  const int err = errno;
  PrintErrno("mount-bind", target, err);
  return FromErrno(err);
}

} // namespace

int main(int argc, char** argv)
{
  if (argc < 2) {
    Usage();
    return static_cast<int>(ExitCode::Usage);
  }

  const std::string_view cmd = argv[1];

  if (cmd == "version" || cmd == "--version" || cmd == "-V") {
    std::printf("fqctl %s\n", kVersion);
    return static_cast<int>(ExitCode::Ok);
  }

  if (cmd == "help" || cmd == "--help" || cmd == "-h") {
    Usage();
    return static_cast<int>(ExitCode::Ok);
  }

  if (cmd == "umount-lazy") {
    if (argc != 3) {
      Usage();
      return static_cast<int>(ExitCode::Usage);
    }
    return static_cast<int>(CmdUmountLazy(argv[2]));
  }

  if (cmd == "chown-r") {
    if (argc != 4) {
      Usage();
      return static_cast<int>(ExitCode::Usage);
    }
    return static_cast<int>(CmdChownR(argv[2], argv[3]));
  }

  if (cmd == "mount-bind") {
    if (argc != 4) {
      Usage();
      return static_cast<int>(ExitCode::Usage);
    }
    return static_cast<int>(CmdMountBind(argv[2], argv[3]));
  }

  std::fprintf(stderr, "fqctl: unknown command '%s'\n", argv[1]);
  Usage();
  return static_cast<int>(ExitCode::Usage);
}
