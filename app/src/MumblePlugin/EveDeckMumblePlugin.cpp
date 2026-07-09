// EveDeck Mumble plugin: forwards talking-state and channel-roster events from the local
// Mumble client to EveDeck over a named pipe, so EveDeck can render its own talker overlay.
//
// Protocol: UTF-8 JSON lines written to \\.\pipe\EveDeckMumble (EveDeck hosts the pipe server;
// this plugin is the client and reconnects with backoff). Events:
//   {"e":"sync","channel":"<name>","users":[{"id":1,"name":"X","state":0},...]}
//   {"e":"talk","id":1,"name":"X","state":1}      state: Mumble_TalkingState (0=passive,1=talking,...)
//   {"e":"join","id":1,"name":"X"}                user entered the local user's channel
//   {"e":"leave","id":1}                          user left the local user's channel
//   {"e":"clear"}                                 server disconnected
//   {"e":"ping"}                                  idle keepalive (see the 5s heartbeat below);
//                                                  EveDeck ignores it, it exists only so a dead
//                                                  pipe is detected even when nobody is talking
//
// The plugin never transmits audio, credentials, or messages -- it is a read-only presence feed.
// GPL-3.0, part of EveDeck (https://github.com/objectless/EveDeck).

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <cstring>
#include <deque>
#include <map>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "vendor/MumblePlugin.h"

namespace {

constexpr const wchar_t *kPipeName = L"\\\\.\\pipe\\EveDeckMumble";

MumbleAPI g_api              = {};
mumble_plugin_id_t g_pluginId = 0;

std::atomic<bool> g_running{ false };
std::thread g_worker;
std::mutex g_queueMutex;
std::condition_variable g_queueCv;
std::deque<std::string> g_queue;

// userID -> name cache for the current connection (guarded by g_stateMutex).
std::mutex g_stateMutex;
std::map<mumble_userid_t, std::string> g_names;
std::atomic<int32_t> g_activeConnection{ -1 };
// Set once mumble_registerAPIFunctions has copied the API struct -- the worker thread must not
// dereference g_api's function pointers before then.
std::atomic<bool> g_apiReady{ false };

std::string JsonEscape(const std::string &s) {
	std::string out;
	out.reserve(s.size() + 8);
	for (unsigned char c : s) {
		switch (c) {
			case '"': out += "\\\""; break;
			case '\\': out += "\\\\"; break;
			case '\b': out += "\\b"; break;
			case '\f': out += "\\f"; break;
			case '\n': out += "\\n"; break;
			case '\r': out += "\\r"; break;
			case '\t': out += "\\t"; break;
			default:
				if (c < 0x20) {
					char buf[8];
					snprintf(buf, sizeof(buf), "\\u%04x", c);
					out += buf;
				} else {
					out += static_cast<char>(c);
				}
		}
	}
	return out;
}

void Enqueue(std::string line) {
	{
		std::lock_guard<std::mutex> lock(g_queueMutex);
		// Bound the queue so a long EveDeck outage can't grow memory unboundedly; old
		// presence events are worthless once superseded anyway.
		if (g_queue.size() > 512)
			g_queue.pop_front();
		g_queue.push_back(std::move(line));
	}
	g_queueCv.notify_one();
}

// Looks up a user's name via the Mumble API, memoized per connection.
std::string LookupUserName(mumble_connection_t connection, mumble_userid_t userID) {
	{
		std::lock_guard<std::mutex> lock(g_stateMutex);
		auto it = g_names.find(userID);
		if (it != g_names.end())
			return it->second;
	}
	std::string name = "?";
	const char *raw  = nullptr;
	if (g_api.getUserName && g_api.getUserName(g_pluginId, connection, userID, &raw) == MUMBLE_STATUS_OK && raw) {
		name = raw;
		if (g_api.freeMemory)
			g_api.freeMemory(g_pluginId, raw);
	}
	std::lock_guard<std::mutex> lock(g_stateMutex);
	g_names[userID] = name;
	return name;
}

// Builds and enqueues a full roster snapshot of the local user's channel. Called on server
// sync, when the local user changes channel, and whenever the pipe (re)connects.
void SendSync(mumble_connection_t connection) {
	if (!g_api.getLocalUserID || !g_api.getChannelOfUser || !g_api.getUsersInChannel)
		return;

	mumble_userid_t localUser = 0;
	if (g_api.getLocalUserID(g_pluginId, connection, &localUser) != MUMBLE_STATUS_OK)
		return;
	mumble_channelid_t channel = 0;
	if (g_api.getChannelOfUser(g_pluginId, connection, localUser, &channel) != MUMBLE_STATUS_OK)
		return;

	std::string channelName;
	const char *rawChannel = nullptr;
	if (g_api.getChannelName
		&& g_api.getChannelName(g_pluginId, connection, channel, &rawChannel) == MUMBLE_STATUS_OK && rawChannel) {
		channelName = rawChannel;
		if (g_api.freeMemory)
			g_api.freeMemory(g_pluginId, rawChannel);
	}

	mumble_userid_t *users = nullptr;
	size_t userCount       = 0;
	if (g_api.getUsersInChannel(g_pluginId, connection, channel, &users, &userCount) != MUMBLE_STATUS_OK)
		return;

	{
		// Channel changed: reset the name cache to the new roster only.
		std::lock_guard<std::mutex> lock(g_stateMutex);
		g_names.clear();
	}

	std::string json = "{\"e\":\"sync\",\"channel\":\"" + JsonEscape(channelName) + "\",\"users\":[";
	for (size_t i = 0; i < userCount; ++i) {
		if (i > 0)
			json += ',';
		json += "{\"id\":" + std::to_string(users[i]) + ",\"name\":\"" + JsonEscape(LookupUserName(connection, users[i]))
				+ "\",\"state\":0}";
	}
	json += "]}";

	if (users && g_api.freeMemory)
		g_api.freeMemory(g_pluginId, users);

	Enqueue(std::move(json));
}

// onServerSynchronized only fires for connections established AFTER the plugin loads. If the
// user enables the plugin while already connected to a server (the common first-run case),
// no callback ever arrives -- so ask Mumble directly for the active, already-synchronized
// connection instead of waiting forever.
void AdoptExistingConnection() {
	if (g_activeConnection.load() >= 0 || !g_apiReady.load())
		return;
	if (!g_api.getActiveServerConnection || !g_api.isConnectionSynchronized)
		return;
	mumble_connection_t active = -1;
	if (g_api.getActiveServerConnection(g_pluginId, &active) != MUMBLE_STATUS_OK || active < 0)
		return;
	bool synced = false;
	if (g_api.isConnectionSynchronized(g_pluginId, active, &synced) != MUMBLE_STATUS_OK || !synced)
		return;
	g_activeConnection.store(active);
	SendSync(active);
}

// Worker thread: owns the pipe client connection, drains the queue, reconnects with backoff.
// On every fresh connect it requests a roster snapshot so EveDeck starts from a known state.
void WorkerLoop() {
	HANDLE pipe = INVALID_HANDLE_VALUE;

	auto closePipe = [&pipe]() {
		if (pipe != INVALID_HANDLE_VALUE) {
			CloseHandle(pipe);
			pipe = INVALID_HANDLE_VALUE;
		}
	};

	auto lastHeartbeat = std::chrono::steady_clock::now();

	while (g_running.load()) {
		if (pipe == INVALID_HANDLE_VALUE) {
			pipe = CreateFileW(kPipeName, GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
			if (pipe == INVALID_HANDLE_VALUE) {
				// EveDeck not running (or pipe busy) -- drop stale queue, retry in 2s.
				{
					std::lock_guard<std::mutex> lock(g_queueMutex);
					g_queue.clear();
				}
				std::unique_lock<std::mutex> lock(g_queueMutex);
				g_queueCv.wait_for(lock, std::chrono::seconds(2));
				continue;
			}
			// Fresh connection: give EveDeck the current roster (adopting a server connection
			// that predates the plugin being enabled, if necessary).
			AdoptExistingConnection();
			int32_t conn = g_activeConnection.load();
			if (conn >= 0)
				SendSync(conn);
			lastHeartbeat = std::chrono::steady_clock::now();
		}

		// Covers enabling the plugin while already connected to a server AND while EveDeck's
		// pipe was already up: the fresh-connect path above ran before the API was usable.
		AdoptExistingConnection();

		std::string line;
		{
			std::unique_lock<std::mutex> lock(g_queueMutex);
			g_queueCv.wait_for(lock, std::chrono::seconds(1), [] { return !g_queue.empty() || !g_running.load(); });
			if (!g_running.load())
				break;
			if (g_queue.empty()) {
				// Nothing to send. Since a write only ever happens on a real talk/roster event,
				// an idle channel would never notice EveDeck restarting out from under an
				// already-open pipe handle -- probe periodically so a dead connection is
				// detected (and reconnected) even when nobody is talking.
				auto now = std::chrono::steady_clock::now();
				if (now - lastHeartbeat >= std::chrono::seconds(5)) {
					lastHeartbeat = now;
					DWORD written = 0;
					if (!WriteFile(pipe, "{\"e\":\"ping\"}\n", 13, &written, nullptr))
						closePipe();
				}
				continue;
			}
			line = std::move(g_queue.front());
			g_queue.pop_front();
		}
		line += '\n';

		DWORD written = 0;
		if (!WriteFile(pipe, line.data(), static_cast<DWORD>(line.size()), &written, nullptr))
			closePipe(); // EveDeck went away; reconnect loop takes over (message dropped by design).
		else
			lastHeartbeat = std::chrono::steady_clock::now();
	}

	closePipe();
}

} // namespace

// ───────────────────────────── mandatory exports ─────────────────────────────

extern "C" {

MUMBLE_PLUGIN_EXPORT mumble_error_t MUMBLE_PLUGIN_CALLING_CONVENTION mumble_init(mumble_plugin_id_t id) {
	g_pluginId = id;
	g_running.store(true);
	g_worker = std::thread(WorkerLoop);
	return MUMBLE_STATUS_OK;
}

MUMBLE_PLUGIN_EXPORT void MUMBLE_PLUGIN_CALLING_CONVENTION mumble_shutdown() {
	g_running.store(false);
	g_queueCv.notify_all();
	if (g_worker.joinable())
		g_worker.join();
}

MUMBLE_PLUGIN_EXPORT struct MumbleStringWrapper MUMBLE_PLUGIN_CALLING_CONVENTION mumble_getName() {
	static const char *name = "EveDeck Talker Bridge";
	return { name, strlen(name), false };
}

MUMBLE_PLUGIN_EXPORT mumble_version_t MUMBLE_PLUGIN_CALLING_CONVENTION mumble_getAPIVersion() {
	return MUMBLE_PLUGIN_API_VERSION;
}

MUMBLE_PLUGIN_EXPORT void MUMBLE_PLUGIN_CALLING_CONVENTION mumble_registerAPIFunctions(void *apiStruct) {
	g_api = MUMBLE_API_CAST(apiStruct);
	g_apiReady.store(true);
}

MUMBLE_PLUGIN_EXPORT void MUMBLE_PLUGIN_CALLING_CONVENTION mumble_releaseResource(const void *) {
	// All strings returned by this plugin are static -- nothing to release.
}

// ───────────────────────────── general information ─────────────────────────────

MUMBLE_PLUGIN_EXPORT mumble_version_t MUMBLE_PLUGIN_CALLING_CONVENTION mumble_getVersion() {
	return { 1, 0, 0 };
}

MUMBLE_PLUGIN_EXPORT struct MumbleStringWrapper MUMBLE_PLUGIN_CALLING_CONVENTION mumble_getAuthor() {
	static const char *author = "EveDeck";
	return { author, strlen(author), false };
}

MUMBLE_PLUGIN_EXPORT struct MumbleStringWrapper MUMBLE_PLUGIN_CALLING_CONVENTION mumble_getDescription() {
	static const char *desc = "Forwards who-is-talking presence to the EveDeck window manager "
							  "over a local named pipe so it can draw a talker overlay. "
							  "Read-only: no audio, chat, or credentials are accessed.";
	return { desc, strlen(desc), false };
}

MUMBLE_PLUGIN_EXPORT uint32_t MUMBLE_PLUGIN_CALLING_CONVENTION mumble_getFeatures() {
	return MUMBLE_FEATURE_NONE;
}

// ───────────────────────────── event callbacks ─────────────────────────────

MUMBLE_PLUGIN_EXPORT void MUMBLE_PLUGIN_CALLING_CONVENTION mumble_onServerSynchronized(mumble_connection_t connection) {
	g_activeConnection.store(connection);
	SendSync(connection);
}

MUMBLE_PLUGIN_EXPORT void MUMBLE_PLUGIN_CALLING_CONVENTION mumble_onServerDisconnected(mumble_connection_t connection) {
	if (g_activeConnection.load() == connection)
		g_activeConnection.store(-1);
	{
		std::lock_guard<std::mutex> lock(g_stateMutex);
		g_names.clear();
	}
	Enqueue("{\"e\":\"clear\"}");
}

MUMBLE_PLUGIN_EXPORT void MUMBLE_PLUGIN_CALLING_CONVENTION mumble_onUserTalkingStateChanged(
	mumble_connection_t connection, mumble_userid_t userID, mumble_talking_state_t talkingState) {
	if (connection != g_activeConnection.load())
		return;
	std::string json = "{\"e\":\"talk\",\"id\":" + std::to_string(userID) + ",\"name\":\""
					   + JsonEscape(LookupUserName(connection, userID))
					   + "\",\"state\":" + std::to_string(static_cast<int>(talkingState)) + "}";
	Enqueue(std::move(json));
}

MUMBLE_PLUGIN_EXPORT void MUMBLE_PLUGIN_CALLING_CONVENTION mumble_onChannelEntered(
	mumble_connection_t connection, mumble_userid_t userID, mumble_channelid_t, mumble_channelid_t newChannel) {
	if (connection != g_activeConnection.load())
		return;

	// If the LOCAL user moved, the whole roster changes -- resync. Otherwise only emit a join
	// when the entering user landed in the local user's channel.
	mumble_userid_t localUser = 0;
	if (g_api.getLocalUserID && g_api.getLocalUserID(g_pluginId, connection, &localUser) == MUMBLE_STATUS_OK
		&& localUser == userID) {
		SendSync(connection);
		return;
	}
	mumble_channelid_t localChannel = 0;
	if (!g_api.getChannelOfUser
		|| g_api.getChannelOfUser(g_pluginId, connection, localUser, &localChannel) != MUMBLE_STATUS_OK
		|| localChannel != newChannel)
		return;
	std::string json = "{\"e\":\"join\",\"id\":" + std::to_string(userID) + ",\"name\":\""
					   + JsonEscape(LookupUserName(connection, userID)) + "\"}";
	Enqueue(std::move(json));
}

MUMBLE_PLUGIN_EXPORT void MUMBLE_PLUGIN_CALLING_CONVENTION mumble_onChannelExited(
	mumble_connection_t connection, mumble_userid_t userID, mumble_channelid_t channel) {
	if (connection != g_activeConnection.load())
		return;
	mumble_userid_t localUser = 0;
	if (g_api.getLocalUserID && g_api.getLocalUserID(g_pluginId, connection, &localUser) == MUMBLE_STATUS_OK
		&& localUser == userID)
		return; // the matching onChannelEntered will resync
	mumble_channelid_t localChannel = 0;
	if (!g_api.getChannelOfUser
		|| g_api.getChannelOfUser(g_pluginId, connection, localUser, &localChannel) != MUMBLE_STATUS_OK
		|| localChannel != channel)
		return;
	Enqueue("{\"e\":\"leave\",\"id\":" + std::to_string(userID) + "}");
}

} // extern "C"
