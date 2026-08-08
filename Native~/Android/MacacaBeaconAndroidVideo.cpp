#include <android/native_window_jni.h>
#include <android/log.h>
#include <EGL/egl.h>
#include <EGL/eglext.h>
#include <GLES3/gl3.h>
#include <jni.h>

#include "IUnityGraphics.h"
#include "IUnityGraphicsVulkan.h"

#include <atomic>
#include <cstdint>
#include <cstdlib>
#include <mutex>
#include <sched.h>
#include <unordered_map>
#include <vector>

#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, "MacacaBeacon", __VA_ARGS__)

namespace
{
    struct VulkanApi
    {
        IUnityGraphicsVulkanV2* unity = nullptr;
        UnityVulkanInstance instance = {};
        PFN_vkCreateAndroidSurfaceKHR createAndroidSurface = nullptr;
        PFN_vkDestroySurfaceKHR destroySurface = nullptr;
        PFN_vkGetPhysicalDeviceSurfaceSupportKHR getSurfaceSupport = nullptr;
        PFN_vkGetPhysicalDeviceSurfaceCapabilitiesKHR getSurfaceCapabilities = nullptr;
        PFN_vkGetPhysicalDeviceSurfaceFormatsKHR getSurfaceFormats = nullptr;
        PFN_vkCreateSwapchainKHR createSwapchain = nullptr;
        PFN_vkDestroySwapchainKHR destroySwapchain = nullptr;
        PFN_vkGetSwapchainImagesKHR getSwapchainImages = nullptr;
        PFN_vkAcquireNextImageKHR acquireNextImage = nullptr;
        PFN_vkQueuePresentKHR queuePresent = nullptr;
        PFN_vkCreateCommandPool createCommandPool = nullptr;
        PFN_vkDestroyCommandPool destroyCommandPool = nullptr;
        PFN_vkAllocateCommandBuffers allocateCommandBuffers = nullptr;
        PFN_vkFreeCommandBuffers freeCommandBuffers = nullptr;
        PFN_vkResetCommandBuffer resetCommandBuffer = nullptr;
        PFN_vkBeginCommandBuffer beginCommandBuffer = nullptr;
        PFN_vkEndCommandBuffer endCommandBuffer = nullptr;
        PFN_vkCmdPipelineBarrier cmdPipelineBarrier = nullptr;
        PFN_vkCmdBlitImage cmdBlitImage = nullptr;
        PFN_vkCreateSemaphore createSemaphore = nullptr;
        PFN_vkDestroySemaphore destroySemaphore = nullptr;
        PFN_vkCreateFence createFence = nullptr;
        PFN_vkDestroyFence destroyFence = nullptr;
        PFN_vkWaitForFences waitForFences = nullptr;
        PFN_vkResetFences resetFences = nullptr;
        PFN_vkQueueSubmit queueSubmit = nullptr;
        bool loaded = false;
    } vulkan;
    IUnityGraphics* unityGraphics = nullptr;

    PFN_vkVoidFunction LoadVk(const char* name)
    {
        return vulkan.instance.getInstanceProcAddr == nullptr ? nullptr : vulkan.instance.getInstanceProcAddr(vulkan.instance.instance, name);
    }

    template <typename T>
    bool LoadVkFunction(T& target, const char* name)
    {
        target = reinterpret_cast<T>(LoadVk(name));
        return target != nullptr;
    }

    struct Session
    {
        ANativeWindow* window = nullptr;
        EGLDisplay display = EGL_NO_DISPLAY;
        EGLSurface surface = EGL_NO_SURFACE;
        EGLContext context = EGL_NO_CONTEXT;
        EGLSurface previousDraw = EGL_NO_SURFACE;
        EGLSurface previousRead = EGL_NO_SURFACE;
        EGLContext previousContext = EGL_NO_CONTEXT;
        bool ownsContext = false;
        GLuint program = 0;
        GLint textureUniform = -1;
        int width = 0;
        int height = 0;
        std::atomic<int> pendingEvents{0};
        bool initialized = false;
        bool vulkanInitialized = false;
        VkSurfaceKHR vulkanSurface = VK_NULL_HANDLE;
        VkSwapchainKHR vulkanSwapchain = VK_NULL_HANDLE;
        VkFormat vulkanFormat = VK_FORMAT_UNDEFINED;
        VkExtent2D vulkanExtent = {};
        VkCommandPool vulkanCommandPool = VK_NULL_HANDLE;
        VkCommandBuffer vulkanCommandBuffer = VK_NULL_HANDLE;
        VkSemaphore vulkanImageAvailable = VK_NULL_HANDLE;
        VkSemaphore vulkanRenderFinished = VK_NULL_HANDLE;
        VkFence vulkanFence = VK_NULL_HANDLE;
        std::vector<VkImage> vulkanImages;
    };

    struct Submit
    {
        long id = 0;
        void* nativeTexture = nullptr;
        long long presentationNanoseconds = 0;
        VkImage sourceImage = VK_NULL_HANDLE;
        VkExtent3D sourceExtent = {};
        VkFormat sourceFormat = VK_FORMAT_UNDEFINED;
        bool sourcePrepared = false;
    };

    std::mutex sessionsMutex;
    std::unordered_map<long, Session*> sessions;

    void OnGraphicsDeviceEvent(UnityGfxDeviceEventType eventType)
    {
        if (eventType == kUnityGfxDeviceEventInitialize && vulkan.unity != nullptr &&
            unityGraphics != nullptr && unityGraphics->GetRenderer() == kUnityGfxRendererVulkan)
        {
            vulkan.instance = vulkan.unity->Instance();
            if (vulkan.instance.instance == VK_NULL_HANDLE)
                return;
            bool ok = true;
            ok = ok && LoadVkFunction(vulkan.createAndroidSurface, "vkCreateAndroidSurfaceKHR");
            ok = ok && LoadVkFunction(vulkan.destroySurface, "vkDestroySurfaceKHR");
            ok = ok && LoadVkFunction(vulkan.getSurfaceSupport, "vkGetPhysicalDeviceSurfaceSupportKHR");
            ok = ok && LoadVkFunction(vulkan.getSurfaceCapabilities, "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
            ok = ok && LoadVkFunction(vulkan.getSurfaceFormats, "vkGetPhysicalDeviceSurfaceFormatsKHR");
            ok = ok && LoadVkFunction(vulkan.createSwapchain, "vkCreateSwapchainKHR");
            ok = ok && LoadVkFunction(vulkan.destroySwapchain, "vkDestroySwapchainKHR");
            ok = ok && LoadVkFunction(vulkan.getSwapchainImages, "vkGetSwapchainImagesKHR");
            ok = ok && LoadVkFunction(vulkan.acquireNextImage, "vkAcquireNextImageKHR");
            ok = ok && LoadVkFunction(vulkan.queuePresent, "vkQueuePresentKHR");
            ok = ok && LoadVkFunction(vulkan.createCommandPool, "vkCreateCommandPool");
            ok = ok && LoadVkFunction(vulkan.destroyCommandPool, "vkDestroyCommandPool");
            ok = ok && LoadVkFunction(vulkan.allocateCommandBuffers, "vkAllocateCommandBuffers");
            ok = ok && LoadVkFunction(vulkan.freeCommandBuffers, "vkFreeCommandBuffers");
            ok = ok && LoadVkFunction(vulkan.resetCommandBuffer, "vkResetCommandBuffer");
            ok = ok && LoadVkFunction(vulkan.beginCommandBuffer, "vkBeginCommandBuffer");
            ok = ok && LoadVkFunction(vulkan.endCommandBuffer, "vkEndCommandBuffer");
            ok = ok && LoadVkFunction(vulkan.cmdPipelineBarrier, "vkCmdPipelineBarrier");
            ok = ok && LoadVkFunction(vulkan.cmdBlitImage, "vkCmdBlitImage");
            ok = ok && LoadVkFunction(vulkan.createSemaphore, "vkCreateSemaphore");
            ok = ok && LoadVkFunction(vulkan.destroySemaphore, "vkDestroySemaphore");
            ok = ok && LoadVkFunction(vulkan.createFence, "vkCreateFence");
            ok = ok && LoadVkFunction(vulkan.destroyFence, "vkDestroyFence");
            ok = ok && LoadVkFunction(vulkan.waitForFences, "vkWaitForFences");
            ok = ok && LoadVkFunction(vulkan.resetFences, "vkResetFences");
            ok = ok && LoadVkFunction(vulkan.queueSubmit, "vkQueueSubmit");
            vulkan.loaded = ok;
            if (ok)
            {
                UnityVulkanPluginEventConfig config = {};
                config.renderPassPrecondition = kUnityVulkanRenderPass_EnsureOutside;
                config.graphicsQueueAccess = kUnityVulkanGraphicsQueueAccess_Allow;
                config.flags = kUnityVulkanEventConfigFlag_EnsurePreviousFrameSubmission;
                vulkan.unity->ConfigureEvent(1, &config);
            }
        }
        else if (eventType == kUnityGfxDeviceEventShutdown)
        {
            vulkan.loaded = false;
            vulkan.instance = {};
        }
    }

    Session* FindSession(long id)
    {
        std::lock_guard<std::mutex> lock(sessionsMutex);
        auto found = sessions.find(id);
        return found == sessions.end() ? nullptr : found->second;
    }

    GLuint CompileShader(GLenum type, const char* source)
    {
        GLuint shader = glCreateShader(type);
        glShaderSource(shader, 1, &source, nullptr);
        glCompileShader(shader);
        GLint compiled = GL_FALSE;
        glGetShaderiv(shader, GL_COMPILE_STATUS, &compiled);
        if (compiled == GL_FALSE)
        {
            char log[1024] = {};
            glGetShaderInfoLog(shader, sizeof(log), nullptr, log);
            LOGE("Android GPU video shader compile failed: %s", log);
            glDeleteShader(shader);
            return 0;
        }
        return shader;
    }

    void DestroyVulkanResources(Session* session);

    bool InitializeSession(Session* session)
    {
        if (session == nullptr || session->window == nullptr)
            return false;
        session->display = eglGetCurrentDisplay();
        session->previousContext = eglGetCurrentContext();
        session->previousDraw = eglGetCurrentSurface(EGL_DRAW);
        session->previousRead = eglGetCurrentSurface(EGL_READ);
        if (session->display == EGL_NO_DISPLAY || session->previousContext == EGL_NO_CONTEXT)
            return false;

        const EGLint windowFormat = ANativeWindow_getFormat(session->window);
        EGLConfig config = nullptr;
        EGLint configCount = 0;
        const EGLint configAttributes[] = {
            EGL_SURFACE_TYPE, EGL_WINDOW_BIT,
            EGL_RENDERABLE_TYPE, EGL_OPENGL_ES3_BIT_KHR,
            EGL_RECORDABLE_ANDROID, EGL_TRUE,
            EGL_NATIVE_VISUAL_ID, windowFormat,
            EGL_RED_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_BLUE_SIZE, 8, EGL_ALPHA_SIZE, 8,
            EGL_NONE
        };
        eglChooseConfig(session->display, configAttributes, &config, 1, &configCount);
        if (configCount == 0)
            return false;

        // The MediaCodec window surface uses a recordable EGLConfig. Create a
        // context with that config and share Unity's texture objects; Unity's
        // own context/config cannot reliably present to this surface.
        const EGLint contextAttributes[] = { EGL_CONTEXT_CLIENT_VERSION, 3, EGL_NONE };
        session->context = eglCreateContext(session->display, config, session->previousContext, contextAttributes);
        session->ownsContext = true;
        if (session->context == EGL_NO_CONTEXT)
        {
            LOGE("Android GPU video eglCreateContext failed: 0x%x", eglGetError());
            return false;
        }
        session->surface = eglCreateWindowSurface(session->display, config, session->window, nullptr);
        if (session->surface == EGL_NO_SURFACE)
        {
            LOGE("Android GPU video eglCreateWindowSurface failed: 0x%x", eglGetError());
            return false;
        }
        if (eglMakeCurrent(session->display, session->surface, session->surface, session->context) != EGL_TRUE)
        {
            LOGE("Android GPU video initial eglMakeCurrent failed: 0x%x", eglGetError());
            return false;
        }

        const char* vertexSource =
            "#version 300 es\n"
            "out vec2 uv;\n"
            "void main() {\n"
            "  const vec2 p[3] = vec2[3](vec2(-1.0, -1.0), vec2(3.0, -1.0), vec2(-1.0, 3.0));\n"
            "  const vec2 t[3] = vec2[3](vec2(0.0, 0.0), vec2(2.0, 0.0), vec2(0.0, 2.0));\n"
            "  gl_Position = vec4(p[gl_VertexID], 0.0, 1.0); uv = t[gl_VertexID];\n"
            "}\n";
        const char* fragmentSource =
            "#version 300 es\n"
            "precision mediump float;\n"
            "in vec2 uv; uniform sampler2D sourceTexture; out vec4 color;\n"
            "void main() { color = texture(sourceTexture, uv); }\n";
        GLuint vertex = CompileShader(GL_VERTEX_SHADER, vertexSource);
        GLuint fragment = CompileShader(GL_FRAGMENT_SHADER, fragmentSource);
        if (vertex == 0 || fragment == 0)
            return false;
        session->program = glCreateProgram();
        glAttachShader(session->program, vertex);
        glAttachShader(session->program, fragment);
        glLinkProgram(session->program);
        glDeleteShader(vertex);
        glDeleteShader(fragment);
        GLint linked = GL_FALSE;
        glGetProgramiv(session->program, GL_LINK_STATUS, &linked);
        if (linked == GL_FALSE)
            return false;
        session->textureUniform = glGetUniformLocation(session->program, "sourceTexture");
        eglQuerySurface(session->display, session->surface, EGL_WIDTH, &session->width);
        eglQuerySurface(session->display, session->surface, EGL_HEIGHT, &session->height);
        session->initialized = session->width > 0 && session->height > 0;
        eglMakeCurrent(session->display, session->previousDraw, session->previousRead, session->previousContext);
        return session->initialized;
    }

    void DestroySessionResources(Session* session)
    {
        if (session == nullptr)
            return;
        DestroyVulkanResources(session);
        if (session->display != EGL_NO_DISPLAY && session->context != EGL_NO_CONTEXT)
        {
            eglMakeCurrent(session->display, session->surface, session->surface, session->context);
            if (session->program != 0) glDeleteProgram(session->program);
            eglMakeCurrent(session->display, session->previousDraw, session->previousRead, session->previousContext);
            if (session->surface != EGL_NO_SURFACE) eglDestroySurface(session->display, session->surface);
            if (session->ownsContext)
                eglDestroyContext(session->display, session->context);
        }
        if (session->window != nullptr)
            ANativeWindow_release(session->window);
        delete session;
    }

    void DestroyVulkanResources(Session* session)
    {
        if (session == nullptr || !vulkan.loaded)
            return;
        if (vulkan.instance.device != VK_NULL_HANDLE)
            vkDeviceWaitIdle(vulkan.instance.device);
        if (session->vulkanFence != VK_NULL_HANDLE) vulkan.destroyFence(vulkan.instance.device, session->vulkanFence, nullptr);
        if (session->vulkanImageAvailable != VK_NULL_HANDLE) vulkan.destroySemaphore(vulkan.instance.device, session->vulkanImageAvailable, nullptr);
        if (session->vulkanRenderFinished != VK_NULL_HANDLE) vulkan.destroySemaphore(vulkan.instance.device, session->vulkanRenderFinished, nullptr);
        if (session->vulkanCommandPool != VK_NULL_HANDLE)
        {
            if (session->vulkanCommandBuffer != VK_NULL_HANDLE)
                vulkan.freeCommandBuffers(vulkan.instance.device, session->vulkanCommandPool, 1, &session->vulkanCommandBuffer);
            vulkan.destroyCommandPool(vulkan.instance.device, session->vulkanCommandPool, nullptr);
        }
        if (session->vulkanSwapchain != VK_NULL_HANDLE) vulkan.destroySwapchain(vulkan.instance.device, session->vulkanSwapchain, nullptr);
        if (session->vulkanSurface != VK_NULL_HANDLE) vulkan.destroySurface(vulkan.instance.instance, session->vulkanSurface, nullptr);
        session->vulkanInitialized = false;
    }

    bool InitializeVulkanSession(Session* session, VkFormat sourceFormat)
    {
        if (session == nullptr || session->window == nullptr || !vulkan.loaded)
            return false;
        VkAndroidSurfaceCreateInfoKHR surfaceInfo = { VK_STRUCTURE_TYPE_ANDROID_SURFACE_CREATE_INFO_KHR };
        surfaceInfo.window = session->window;
        if (vulkan.createAndroidSurface(vulkan.instance.instance, &surfaceInfo, nullptr, &session->vulkanSurface) != VK_SUCCESS)
            return false;

        VkBool32 supported = VK_FALSE;
        if (vulkan.getSurfaceSupport(vulkan.instance.physicalDevice, vulkan.instance.queueFamilyIndex, session->vulkanSurface, &supported) != VK_SUCCESS || !supported)
            return false;
        uint32_t formatCount = 0;
        if (vulkan.getSurfaceFormats(vulkan.instance.physicalDevice, session->vulkanSurface, &formatCount, nullptr) != VK_SUCCESS || formatCount == 0)
            return false;
        std::vector<VkSurfaceFormatKHR> formats(formatCount);
        if (vulkan.getSurfaceFormats(vulkan.instance.physicalDevice, session->vulkanSurface, &formatCount, formats.data()) != VK_SUCCESS)
            return false;
        VkSurfaceFormatKHR selected = formats[0];
        bool selectedSourceFormat = false;
        for (const auto& format : formats)
        {
            if (format.format == sourceFormat)
            {
                selected = format;
                selectedSourceFormat = true;
                break;
            }
        }
        if (!selectedSourceFormat)
        {
            for (const auto& format : formats)
            {
                if (format.format == VK_FORMAT_B8G8R8A8_UNORM || format.format == VK_FORMAT_R8G8B8A8_UNORM)
                {
                    selected = format;
                    break;
                }
            }
        }
        VkSurfaceCapabilitiesKHR capabilities = {};
        if (vulkan.getSurfaceCapabilities(vulkan.instance.physicalDevice, session->vulkanSurface, &capabilities) != VK_SUCCESS)
            return false;
        session->vulkanFormat = selected.format;
        session->vulkanExtent = capabilities.currentExtent.width == 0xFFFFFFFFu
            ? VkExtent2D{ static_cast<uint32_t>(session->width), static_cast<uint32_t>(session->height) }
            : capabilities.currentExtent;
        uint32_t imageCount = capabilities.minImageCount + 1;
        if (capabilities.maxImageCount != 0 && imageCount > capabilities.maxImageCount)
            imageCount = capabilities.maxImageCount;
        VkSwapchainCreateInfoKHR swapchainInfo = { VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR };
        swapchainInfo.surface = session->vulkanSurface;
        swapchainInfo.minImageCount = imageCount;
        swapchainInfo.imageFormat = selected.format;
        swapchainInfo.imageColorSpace = selected.colorSpace;
        swapchainInfo.imageExtent = session->vulkanExtent;
        swapchainInfo.imageArrayLayers = 1;
        swapchainInfo.imageUsage = VK_IMAGE_USAGE_TRANSFER_DST_BIT;
        swapchainInfo.imageSharingMode = VK_SHARING_MODE_EXCLUSIVE;
        swapchainInfo.preTransform = capabilities.currentTransform;
        swapchainInfo.compositeAlpha = VK_COMPOSITE_ALPHA_INHERIT_BIT_KHR;
        swapchainInfo.presentMode = VK_PRESENT_MODE_FIFO_KHR;
        swapchainInfo.clipped = VK_TRUE;
        if (vulkan.createSwapchain(vulkan.instance.device, &swapchainInfo, nullptr, &session->vulkanSwapchain) != VK_SUCCESS)
            return false;
        uint32_t actualCount = 0;
        vulkan.getSwapchainImages(vulkan.instance.device, session->vulkanSwapchain, &actualCount, nullptr);
        session->vulkanImages.resize(actualCount);
        if (vulkan.getSwapchainImages(vulkan.instance.device, session->vulkanSwapchain, &actualCount, session->vulkanImages.data()) != VK_SUCCESS)
            return false;

        VkCommandPoolCreateInfo poolInfo = { VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO };
        poolInfo.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
        poolInfo.queueFamilyIndex = vulkan.instance.queueFamilyIndex;
        if (vulkan.createCommandPool(vulkan.instance.device, &poolInfo, nullptr, &session->vulkanCommandPool) != VK_SUCCESS)
            return false;
        VkCommandBufferAllocateInfo allocInfo = { VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO };
        allocInfo.commandPool = session->vulkanCommandPool;
        allocInfo.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
        allocInfo.commandBufferCount = 1;
        if (vulkan.allocateCommandBuffers(vulkan.instance.device, &allocInfo, &session->vulkanCommandBuffer) != VK_SUCCESS)
            return false;
        VkSemaphoreCreateInfo semaphoreInfo = { VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO };
        if (vulkan.createSemaphore(vulkan.instance.device, &semaphoreInfo, nullptr, &session->vulkanImageAvailable) != VK_SUCCESS ||
            vulkan.createSemaphore(vulkan.instance.device, &semaphoreInfo, nullptr, &session->vulkanRenderFinished) != VK_SUCCESS)
            return false;
        VkFenceCreateInfo fenceInfo = { VK_STRUCTURE_TYPE_FENCE_CREATE_INFO, nullptr, VK_FENCE_CREATE_SIGNALED_BIT };
        if (vulkan.createFence(vulkan.instance.device, &fenceInfo, nullptr, &session->vulkanFence) != VK_SUCCESS)
            return false;
        session->vulkanInitialized = true;
        return true;
    }

    void VulkanBarrier(VkCommandBuffer commandBuffer, VkImage image, VkImageLayout oldLayout, VkImageLayout newLayout,
        VkAccessFlags sourceAccess, VkAccessFlags destinationAccess, VkPipelineStageFlags sourceStage, VkPipelineStageFlags destinationStage)
    {
        VkImageMemoryBarrier barrier = { VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER };
        barrier.srcAccessMask = sourceAccess;
        barrier.dstAccessMask = destinationAccess;
        barrier.oldLayout = oldLayout;
        barrier.newLayout = newLayout;
        barrier.image = image;
        barrier.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
        barrier.subresourceRange.levelCount = 1;
        barrier.subresourceRange.layerCount = 1;
        vulkan.cmdPipelineBarrier(commandBuffer, sourceStage, destinationStage, 0, 0, nullptr, 0, nullptr, 1, &barrier);
    }

    bool PrepareVulkanSubmit(Submit* submit)
    {
        if (submit == nullptr || !vulkan.loaded || vulkan.unity == nullptr || submit->nativeTexture == nullptr)
            return false;
        UnityVulkanImage image = {};
        if (!vulkan.unity->AccessTexture(submit->nativeTexture, UnityVulkanWholeImage,
            VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, VK_PIPELINE_STAGE_TRANSFER_BIT,
            VK_ACCESS_TRANSFER_READ_BIT, kUnityVulkanResourceAccess_PipelineBarrier, &image))
            return false;
        submit->sourceImage = image.image;
        submit->sourceExtent = image.extent;
        submit->sourceFormat = image.format;
        submit->sourcePrepared = submit->sourceImage != VK_NULL_HANDLE && image.extent.width > 0 && image.extent.height > 0;
        return submit->sourcePrepared;
    }

    bool RenderVulkanSubmit(Submit* submit)
    {
        Session* session = FindSession(submit == nullptr ? 0 : submit->id);
        if (session == nullptr || submit == nullptr || !submit->sourcePrepared || !vulkan.loaded)
            return false;
        if (!session->vulkanInitialized && !InitializeVulkanSession(session, submit->sourceFormat))
            return false;
        if (vulkan.waitForFences(vulkan.instance.device, 1, &session->vulkanFence, VK_TRUE, UINT64_MAX) != VK_SUCCESS ||
            vulkan.resetFences(vulkan.instance.device, 1, &session->vulkanFence) != VK_SUCCESS)
            return false;
        uint32_t imageIndex = 0;
        VkResult acquire = vulkan.acquireNextImage(vulkan.instance.device, session->vulkanSwapchain, UINT64_MAX, session->vulkanImageAvailable, VK_NULL_HANDLE, &imageIndex);
        if (acquire != VK_SUCCESS && acquire != VK_SUBOPTIMAL_KHR)
            return false;
        vulkan.resetCommandBuffer(session->vulkanCommandBuffer, 0);
        VkCommandBufferBeginInfo beginInfo = { VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO };
        if (vulkan.beginCommandBuffer(session->vulkanCommandBuffer, &beginInfo) != VK_SUCCESS)
            return false;
        VkImage source = submit->sourceImage;
        VulkanBarrier(session->vulkanCommandBuffer, session->vulkanImages[imageIndex], VK_IMAGE_LAYOUT_UNDEFINED, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
            0, VK_ACCESS_TRANSFER_WRITE_BIT, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT);
        VkImageBlit blit = {};
        blit.srcSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
        blit.srcSubresource.layerCount = 1;
        blit.srcOffsets[1] = { static_cast<int32_t>(submit->sourceExtent.width), static_cast<int32_t>(submit->sourceExtent.height), 1 };
        blit.dstSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
        blit.dstSubresource.layerCount = 1;
        blit.dstOffsets[1] = { static_cast<int32_t>(session->vulkanExtent.width), static_cast<int32_t>(session->vulkanExtent.height), 1 };
        vulkan.cmdBlitImage(session->vulkanCommandBuffer, source, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
            session->vulkanImages[imageIndex], VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &blit, VK_FILTER_NEAREST);
        VulkanBarrier(session->vulkanCommandBuffer, session->vulkanImages[imageIndex], VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, VK_IMAGE_LAYOUT_PRESENT_SRC_KHR,
            VK_ACCESS_TRANSFER_WRITE_BIT, 0, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT);
        if (vulkan.endCommandBuffer(session->vulkanCommandBuffer) != VK_SUCCESS)
            return false;
        VkPipelineStageFlags waitStage = VK_PIPELINE_STAGE_TRANSFER_BIT;
        VkSubmitInfo submitInfo = { VK_STRUCTURE_TYPE_SUBMIT_INFO };
        submitInfo.waitSemaphoreCount = 1;
        submitInfo.pWaitSemaphores = &session->vulkanImageAvailable;
        submitInfo.pWaitDstStageMask = &waitStage;
        submitInfo.commandBufferCount = 1;
        submitInfo.pCommandBuffers = &session->vulkanCommandBuffer;
        submitInfo.signalSemaphoreCount = 1;
        submitInfo.pSignalSemaphores = &session->vulkanRenderFinished;
        if (vulkan.queueSubmit(vulkan.instance.graphicsQueue, 1, &submitInfo, session->vulkanFence) != VK_SUCCESS)
            return false;
        VkPresentInfoKHR presentInfo = { VK_STRUCTURE_TYPE_PRESENT_INFO_KHR };
        presentInfo.waitSemaphoreCount = 1;
        presentInfo.pWaitSemaphores = &session->vulkanRenderFinished;
        presentInfo.swapchainCount = 1;
        presentInfo.pSwapchains = &session->vulkanSwapchain;
        presentInfo.pImageIndices = &imageIndex;
        return vulkan.queuePresent(vulkan.instance.graphicsQueue, &presentInfo) == VK_SUCCESS;
    }

    void RenderSubmit(Submit* submit)
    {
        Session* session = FindSession(submit == nullptr ? 0 : submit->id);
        if (session == nullptr || submit->nativeTexture == nullptr)
            return;
        if (vulkan.loaded)
        {
            if (!RenderVulkanSubmit(submit))
                LOGE("Could not submit Vulkan frame to encoder surface");
            return;
        }
        if (!session->initialized && !InitializeSession(session))
        {
            LOGE("Could not initialize EGL encoder surface");
            return;
        }

        session->previousContext = eglGetCurrentContext();
        session->previousDraw = eglGetCurrentSurface(EGL_DRAW);
        session->previousRead = eglGetCurrentSurface(EGL_READ);
        if (eglMakeCurrent(session->display, session->surface, session->surface, session->context) != EGL_TRUE)
        {
            LOGE("Android GPU video frame eglMakeCurrent failed: 0x%x", eglGetError());
            return;
        }

        GLuint sourceTexture = static_cast<GLuint>(reinterpret_cast<uintptr_t>(submit->nativeTexture));
        glViewport(0, 0, session->width, session->height);
        glUseProgram(session->program);
        glActiveTexture(GL_TEXTURE0);
        glBindTexture(GL_TEXTURE_2D, sourceTexture);
        glUniform1i(session->textureUniform, 0);
        glDrawArrays(GL_TRIANGLES, 0, 3);
        glBindTexture(GL_TEXTURE_2D, 0);
        glUseProgram(0);

        auto presentation = reinterpret_cast<PFNEGLPRESENTATIONTIMEANDROIDPROC>(eglGetProcAddress("eglPresentationTimeANDROID"));
        if (presentation != nullptr)
            presentation(session->display, session->surface, submit->presentationNanoseconds);
        if (eglSwapBuffers(session->display, session->surface) != EGL_TRUE)
            LOGE("Android GPU video eglSwapBuffers failed: 0x%x", eglGetError());
        eglMakeCurrent(session->display, session->previousDraw, session->previousRead, session->previousContext);
    }
}

extern "C" JNIEXPORT jint JNICALL Java_com_macacagames_beacon_MacacaBeaconVideo_nativeRegisterSurface(
    JNIEnv* env, jclass, jlong id, jobject surface)
{
    if (surface == nullptr)
        return 0;
    auto* session = new Session();
    session->window = ANativeWindow_fromSurface(env, surface);
    if (session->window == nullptr)
    {
        delete session;
        return 0;
    }
    session->width = ANativeWindow_getWidth(session->window);
    session->height = ANativeWindow_getHeight(session->window);
    std::lock_guard<std::mutex> lock(sessionsMutex);
    sessions[static_cast<long>(id)] = session;
    return 1;
}

extern "C" JNIEXPORT void JNICALL Java_com_macacagames_beacon_MacacaBeaconVideo_nativeWaitForIdle(
    JNIEnv*, jclass, jlong id)
{
    Session* session = FindSession(static_cast<long>(id));
    if (session == nullptr)
        return;
    while (session->pendingEvents.load() != 0)
        sched_yield();
}

extern "C" JNIEXPORT void JNICALL Java_com_macacagames_beacon_MacacaBeaconVideo_nativeUnregisterSurface(
    JNIEnv*, jclass, jlong id)
{
    Session* session = nullptr;
    {
        std::lock_guard<std::mutex> lock(sessionsMutex);
        auto found = sessions.find(static_cast<long>(id));
        if (found == sessions.end()) return;
        session = found->second;
        sessions.erase(found);
    }
    while (session->pendingEvents.load() != 0)
        sched_yield();
    DestroySessionResources(session);
}

static void MacacaBeaconAndroidVideo_RenderEvent(int eventId, void* data)
{
    if (eventId == 1 || eventId == 2)
    {
        auto* submit = static_cast<Submit*>(data);
        if (eventId == 2)
        {
            PrepareVulkanSubmit(submit);
            return;
        }
        RenderSubmit(submit);
        if (submit != nullptr)
        {
            Session* session = FindSession(submit->id);
            if (session != nullptr) session->pendingEvents.fetch_sub(1);
        }
        delete submit;
    }
}

extern "C" void* MacacaBeaconAndroidVideo_GetRenderEventFunc()
{
    return reinterpret_cast<void*>(&MacacaBeaconAndroidVideo_RenderEvent);
}

extern "C" UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* interfaces)
{
    if (interfaces == nullptr)
        return;
    unityGraphics = interfaces->Get<IUnityGraphics>();
    vulkan.unity = interfaces->Get<IUnityGraphicsVulkanV2>();
    if (unityGraphics != nullptr)
        unityGraphics->RegisterDeviceEventCallback(OnGraphicsDeviceEvent);
    if (unityGraphics != nullptr && unityGraphics->GetRenderer() == kUnityGfxRendererVulkan)
        OnGraphicsDeviceEvent(kUnityGfxDeviceEventInitialize);
}

extern "C" UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API UnityPluginUnload()
{
    vulkan.unity = nullptr;
    vulkan.loaded = false;
    unityGraphics = nullptr;
}

extern "C" void* MacacaBeaconAndroidVideo_AllocateSubmitData(long id, void* nativeTexture, double seconds)
{
    Session* session = FindSession(id);
    if (session == nullptr || nativeTexture == nullptr)
        return nullptr;
    auto* submit = new Submit();
    submit->id = id;
    submit->nativeTexture = nativeTexture;
    submit->presentationNanoseconds = static_cast<long long>(seconds * 1000000000.0);
    session->pendingEvents.fetch_add(1);
    return submit;
}
